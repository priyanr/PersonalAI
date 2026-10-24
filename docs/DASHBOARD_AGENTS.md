# Dashboard & Alerts Agents

Design for `AgentType.DashboardAlerts`: an agent that collects data from monitored targets on a short
interval, stores it, and uses the LLM on its own schedule to design a dashboard and alert rules.
The first use case is server health monitoring; the data/dashboard/alert layers are deliberately generic.

## Status

| Step | Scope | State |
|------|-------|-------|
| 1 | `Agent.Type`, `TypeConfigJson`, descriptor registry, agent-editor settings | **Done** |
| 2 | Targets/groups, SSH runner, test-connection, targets page | **Done** |
| 3 | Datasets/DataPoints, health collectors, `health_discover`/`health_report`/`data_query`, collect hosted service | **Done** |
| 4 | Dashboard versions, `dashboard_*` tools, frontend widgets | **Done** |
| 5 | Alert rules/events, in-app alert list, `alert_rule_save`, retention | **Done** |
| 7 | Tool-driven collection (v2): SSH tool, allowlist guard, model-designed jobs, approval | **Done** |
| 8 | Generic data sources (v3): per-agent tool scopes, `IDataSource`, scope picker | **Done** |
| 6 | Notification action dispatcher (alert -> skill/tool) | Later |

## Decisions

- **Transport**: SSH, agentless, read-only fixed command templates, run in-process from the API via SSH.NET (not the sandbox broker: it gives host-key pinning without a container per probe). Credentials
  come from the secrets vault; the LLM only ever sees target IDs.
- **Collect interval**: per-agent setting `collectIntervalMinutes`, validated to 1-5.
- **Dashboards**: exactly one per agent. The LLM designs it (`dashboard_save`); every save is a version.
- **Alerts**: in-app list first. `AlertRule.ActionJson` / `AlertEvent.ActionStatus` exist from the start so the
  later notification step (run a skill/tool when an alert fires/resolves) needs no schema change.

## Two loops

| Loop | Runs | LLM | Job |
|------|------|-----|-----|
| Collect | every `collectIntervalMinutes`, hosted service | No | run collector tools per target, persist datasets, evaluate alert rules |
| Interpret | agent's normal schedule, manual, or on a critical alert | Yes (`AgentExecutionService`) | discovery review, `data_query`, `dashboard_save`, `alert_rule_save`, summary |

The dashboard reads live data, so it stays fresh between LLM runs.

## Agent type mechanism (implemented)

- `Agent.Type` (`General` default) and `Agent.TypeConfigJson` (`{}` for General).
- `IAgentTypeDescriptor` (one per type, registered in DI, resolved via `AgentTypeRegistry`):
  `NormalizeConfig` validates and normalizes the type settings; `SystemPromptPreamble` is prepended to the
  agent's own prompt in `AgentExecutionService`.
- `DashboardAlertsConfig`: `collectIntervalMinutes` (1-5, default 5), `discoveryRefreshHours` (default 24),
  `retentionDays` (default 30), `targetGroupIds` (groups to monitor).
- API: `GET /api/agents/types`; `type` and `typeConfig` on create/update/get.

## Planned entities

```
SshServerGroup      Id, Name, Description
SshServer           Id, GroupId, Name, Address, Port, Kind, Username, SecretRef, Tags, Enabled, LastSeenAt, LastDiscoveryAt
AgentTargetGroup AgentId, GroupId
Dataset          Id, AgentId, Name, Kind(timeseries|snapshot|event), SchemaHintJson, RetentionDays
DataPoint        Id, DatasetId, EntityKey, Ts, ValuesJson          idx(DatasetId, EntityKey, Ts)
DataRollup       DatasetId, EntityKey, Bucket(1h|1d), Ts, AggJson
Dashboard        Id, AgentId(unique), CurrentVersion
DashboardVersion DashboardId, Version, SpecJson, Reason, CreatedAt, AgentRunId
AlertRule        Id, AgentId, Name, Dataset, EntityFilter, ConditionJson, ForMinutes, Severity, Enabled, Source(user|llm), ActionJson
AlertEvent       Id, RuleId, EntityKey, State(firing|resolved|acked), FirstSeen, LastSeen, ResolvedAt, Value, Message, ActionStatus
```

## Planned tools

`health_discover`, `health_report` (collectors), `data_query`, `data_write` (derived metrics only),
`dashboard_save`/`dashboard_get`, `alert_rule_save`/`alert_list`. Collectors implement `IDatasetProducer`
(dataset name, kind, entity key) and the runner persists their output - the LLM never has to store collected data.

## Dashboard spec

Declarative JSON, never HTML/JS. Widgets (stat, gauge, timeseries, bar, table, statusGrid, markdown) bind to
queries over datasets and are rendered by the frontend from `/api/dashboards/{id}/data`. The `dashboard_save`
schema lists allowed widgets and is validated server-side.

## SSH safety

Fixed command templates (no LLM-supplied shell), host-key pinning (TOFU) on `SshServer`, per-target timeout,
concurrency cap. An unreachable target is recorded as a datapoint so it can be alerted on.

## Extending

A new use case = a target kind + collector tools with an output contract + an agent prompt (+ widgets if needed).

## SSH servers (implemented, step 2)

- `SshServerGroup` / `SshServer` entities (`AddTargets` migration, later renamed to `SshServers`). A target stores `SecretRef`, the key of a secret
  whose value is a PEM private key or a password; credentials never live on the target.
- `ISshRunner` (`SshNetRunner`) runs one fixed command with a timeout. Host keys are pinned on first success
  (trust on first use, SHA256 fingerprint); a mismatch fails the connection. Changing address, port or username
  clears the pin, and `POST /api/ssh/servers/{id}/reset-host-key` clears it explicitly.
- `SshServersService.TestConnection` runs the probe `uname -sr`, records `LastSeenAt` / `LastError` (`SshServer.RecordConnection`), and pins the key only
  after a successful, authenticated connection.
- UI: Settings -> Tools -> SSH (groups, servers, test connection, reset host key). The agent form selects which
  groups a Dashboard & Alerts agent monitors.

## Collection (implemented, step 3)

- **Data layer**: `Dataset` (per agent, unique by name; kind Timeseries/Snapshot/Event) and `DataPoint`
  (`EntityKey`, UTC `Ts`, flat `ValuesJson`). Nothing in it is server-specific. `IDatasetRepository` appends,
  queries (window, latest-per-entity, entity/prefix filter) and prunes points older than the retention, in every
  dataset kind.
- **Collectors**: `HealthScripts` holds two fixed read-only shell scripts (report, discovery) that emit
  `@@section` blocks; `HealthParser` turns them into typed models, tolerating missing tools (no docker, no
  sensors, no systemd). Verified against a real Ubuntu host over real SSH (key auth, host-key pin and
  mismatch rejection, both scripts, storage). Host keys are stored as `SHA256:<base64>`, the form
  `ssh-keygen -l` prints.
- **Datasets written**: `system` (one entity per server: cpu%, load, memory, swap, uptime, `reachable`),
  `disk` (per mount), `temperature` (per sensor), `container` (snapshot; only when docker is accessible),
  `discovery` (snapshot inventory). Entity keys start with the target id; the display name is in the values.
  An unreachable server writes `system` with `reachable:false` and the error, so it can be alerted on.
- **`CollectionService`** (singleton): one pass per agent at a time (a second concurrent pass is skipped),
  up to 4 targets in parallel, each in its own DI scope. Discovery is refreshed when older than
  `discoveryRefreshHours`. `CollectionHostedService` runs it every `collectIntervalMinutes` for enabled
  Dashboard & Alerts agents and prunes hourly using `retentionDays`.
- **Tools** (always available to Dashboard & Alerts agents via `IAgentTypeDescriptor.RequiredTools`, and
  scoped to the calling agent through `ToolExecutionContext.AgentId`): `health_discover`, `health_report`
  (both take optional server names) and `data_query` (lists datasets, windows, `latest`, `target`,
  `bucketMinutes` averaging for long ranges).
- **API**: `GET /api/agents/{id}/datasets`, `GET /api/agents/{id}/datasets/{name}/points`,
  `POST /api/agents/{id}/collect-now?discover=`. The agent page shows a Collected data panel with these.

## Dashboards (implemented, step 4)

- **One dashboard per agent**, saved as immutable versions (`Dashboard` + `DashboardVersion`, newest 50 kept).
  Restoring an old version saves it as a new one, so history is never rewritten.
- **The spec is declarative JSON** (`DashboardSpec`): a title and up to 30 widgets, each bound to a query over the
  agent's datasets. Widget types: `stat`, `gauge`, `timeseries`, `bar`, `statusGrid`, `table`, `markdown`. Query:
  `{dataset, field, target?, filter?, agg?, windowMinutes?, bucketMinutes?}`; options: `{unit, min, max, warn, crit,
  lowerIsWorse, columns, limit, sort}`. Unknown properties are rejected, so a model's typo is reported rather than
  ignored. A model never writes markup or script; markdown is rendered from React elements, never `innerHTML`.
- **Two-stage validation** (`DashboardSpecValidator`, then `DashboardService.ValidateAsync`): structure first, then
  every dataset, field, filter key, column and server name is checked against what has really been collected, with
  the available alternatives in the error ("field 'cpu' not found; available: cpuPercent, …"). Nothing is saved on
  error.
- **Live data**: `DashboardDataBuilder` runs each widget's query on every request and shapes the result, so the
  dashboard stays fresh between model runs and the model designs rather than snapshots. A failing widget returns an
  error for that widget only. Boolean fields (`reachable`, `running`) read as ok/critical; numeric fields get status
  from `warn`/`crit`. Timeseries cap at 8 series (the worst are shown, the rest counted), never a ninth colour.
- **Tools** `dashboard_get` (spec + version + a preview of current widget data) and `dashboard_save` (spec +
  reason; returns the same preview so the model can see empty or broken widgets). Both are always available to
  Dashboard & Alerts agents.
- **API**: `GET /api/agents/{id}/dashboard[?version=]` (204 if none), `.../dashboard/data[?version=]`,
  `.../dashboard/versions`, `POST .../dashboard/versions/{n}/restore`.
- **UI**: Dashboards in the nav lists Dashboard & Alerts agents; each opens `/agents/{id}/dashboard`: live widgets
  (polled at the spec's `refreshSeconds`, holding the previous frame at reduced opacity while refetching), version
  picker with restore, and "Regenerate with AI" (runs the agent). Charts are hand-built SVG/HTML, with no chart
  library: hover crosshair + tooltip listing every series, keyboard arrows, legend for 2+ series, end-of-line values
  where they do not collide, warn/crit reference lines, and a "Show table" view of every plotted value.
- **Colour**: the categorical palette from the dataviz skill, validated against this app's panel surfaces in light
  and dark (all hard checks pass; four light-mode hues sit under 3:1 contrast, which the legend, direct values and
  table view cover). Status colours are reserved for state and always shown with an icon and a word.

## Alerts (implemented, step 5)

- **Rules** (`AlertRule`, per agent): a condition on one numeric or boolean field of a dataset:
  `{dataset, field, operator (gt|gte|lt|lte|eq|ne), threshold, forMinutes, severity (warning|critical), target?,
  filter?}`. Booleans read as 1/0, so `system.reachable eq 0` is a down-server rule. `Source` is `user` or `llm`;
  a model may edit or delete only rules it authored. At most 50 rules per agent, unique names.
- **Events** (`AlertEvent`): one per (rule, entity), with states Pending, Firing and Resolved. Pending is the
  `forMinutes` hold period and is never shown. Resolved events are history, pruned with the agent's
  `retentionDays`. Acknowledging sticks until the alert resolves.
- **Evaluation** (`AlertEvaluator`) runs after every collection pass that took readings: model-free and
  deterministic, using the newest point per matching entity. Hold time is measured in data-point time, so results
  don't depend on when evaluation runs. Samples older than max(15 min, 3x the collect interval) are ignored
  entirely: stale data neither opens nor resolves an alert, so a collector outage cannot flap them. A failing rule
  or evaluator is logged and never fails collection. Disabling a rule resolves what it had open; deleting it
  removes its events.
- **Validation** (`AlertRuleService`): the same approach as dashboards. Dataset, field (must be numeric or
  boolean), filter keys and server name are checked against collected data, with the alternatives in the error.
  With nothing collected yet only structure is checked, so rules can be created on a fresh agent.
- **Model tools** `alert_list`, `alert_rule_save` (upsert by name), `alert_rule_delete`, always available to
  Dashboard & Alerts agents. The run preamble tells the model to call `alert_list` first and not to create noisy
  rules.
- **API**: `GET/POST /api/agents/{id}/alert-rules`, `PATCH/DELETE .../alert-rules/{ruleId}`,
  `GET /api/alerts?state=active|resolved|all[&agentId=]`, `GET /api/alerts/summary`,
  `POST /api/alerts/{id}/acknowledge`.
- **UI**: an Alerts page (Active / Resolved 7 days, acknowledge) with a count badge in the nav; an "N active
  alerts" strip above every dashboard; and an Alert rules panel on the agent page (presets for server down, high
  CPU, high memory, disk nearly full, overheating, plus custom rules; rules written by the model are labelled).
- **Notifications** are the next step: `AlertRule.ActionJson` and `AlertEvent.ActionStatus` already exist, so
  running a skill or tool when an alert fires or resolves needs no schema change.

## Review hardening

Changes made after a code review of steps 1-5:

- **SSH secrets are restricted.** A target stores a secret name, and connecting sends that secret's value to the
  target's address, so an unrestricted name would let anyone who can create a target deliver any vault secret (an
  API key) to a host they control. Only secrets named `SSH_<something>` can be used by targets
  (`SshSecrets`): enforced when a target is saved and again in the runner, and the target form only offers those.
  Targets can still point at any address, so a caller with API access can probe hosts reachable from the server;
  keep the API behind authentication (`AUTH_ENABLED`) on anything shared.
- **Stale rows no longer look current.** Snapshot datasets are pruned like the others (the newest point is no longer
  kept forever). Widgets that show the latest value per entity ignore rows older than an hour
  (`DashboardDataBuilder.StaleAfter`), so a removed container or deleted server drops off instead of showing its
  last state. Time series still draw history within their window.
- **Alerts on entities that disappear are closed.** When an entity no longer matches the rule (the rule was edited),
  stops reporting the field (a server that went down has no CPU reading), or stops reporting at all for over an
  hour, its open alert resolves, but "stopped reporting" only counts while the rest of the dataset is still
  updating, so a collector outage does not resolve everything and re-fire it on recovery. Editing the dataset,
  field, server or filter of a rule closes its open alerts at once; changing only the threshold or operator leaves
  them to the next evaluation.
- **PATCH /api/agents/{id} keeps the type.** Omitting `type` (and `typeConfig`) leaves the agent's type and
  settings untouched; a different type without a config gets that type's defaults.
- **Alert queries run in the database.** The state and date filters are part of the SQL query, and the nav badge's
  `/api/alerts/summary` is one joined count over firing events instead of loading all history.
- **Server names resolve within the calling agent.** `data_query`'s `target` is looked up among that agent's
  monitored groups only (names are unique per group, not across the fleet); the same name in two of its groups
  returns both.
- **Long chart windows are complete.** A time series widget picks the worst eight entities from their latest values,
  then fetches just those with a per-entity point cap, instead of one global newest-20,000 cap that silently cut the
  oldest part of a long window once the fleet grew.
- **Docker.** The API image runs as a non-root user; a one-shot `api-init` compose service chowns the bind-mounted
  data folders to its UID so the SQLite db and workspace stay writable (see Deployment).

## Tool-driven collection (v2)

Steps 1-5 above built the first version, where fixed Linux health collectors live in code. v2 makes collection
**data the model designs**, so monitoring anything reachable by a tool (Kubernetes, a database, a custom CLI) is a
prompt, not a code change. This supersedes the fixed `health_discover` / `health_report` tools and the Targets page.

### Principle: the model designs jobs; the platform runs them

A model run uses tools to explore (`ssh`), then saves what works as a **collection job** (`collector_save`). The
platform runs saved jobs on their own interval with **no model involved** and writes the results to datasets.
Dashboards and alerts read datasets exactly as before. The model never runs on every tick: that would cost a call per
interval, leave gaps when a run fails, and put an LLM issuing shell commands to your servers unattended.

### Decisions

- **Free-form commands with an allowlist**, enforced in code, not by prompt (below).
- **Model-created jobs need the user's approval** before they run unattended (per-agent setting, default on). The
  approval UI shows the exact command. A model edit to an active job's command, parser or dataset sends it back to
  pending approval.
- **Jobs belong to one agent.**
- **No optional per-tick model mode**: scheduled runs plus ad-hoc commands during runs cover it.

### Parts

- **SSH is a tool** with its settings under Settings > Tools: the command allowlist and the target groups/servers
  (the Targets page moves there as a custom settings component). SQL later is a second tool with its own
  connections and the same job runner.
- **`CollectionJob`** (per agent): `name, command (request), intervalMinutes, parser, dataset, targets?, status`
  where status is PendingApproval | Active | Disabled. `Source` is `user`, `llm` or `builtin`. Run state
  (`LastRunAt`, `LastError`, `LastRowCount`) is stored on the job.
- **Parser** (declarative JSON): `table` (whitespace columns, typed), `json` (dotted path to an array or object plus
  field paths), `kv` (`key=value` / `key: value` lines), `regex` (named groups per line), or `builtin`. Output
  is rows; the platform adds `target` (the server name) to every row and keys entities by server plus the
  dataset's `keyFields`.
- **Dataset spec**: `name`, `kind` (timeseries | snapshot | event), `keyFields`. Everything downstream (queries,
  widgets, alert rules, retention) is unchanged.
- **Built-in template "Linux host health"** *(removed; see "Generic collection" at the end)*: it was the first
  version's scripts and parsers as two builtin jobs. The model now designs every job.
- **Model tools**: `ssh` (one allowlisted command on one of the agent's servers, for exploring), `collector_test`
  (dry run: runs it, parses it, shows sample rows, saves nothing), `collector_save` (validates, dry-runs, saves,
  pending approval), `collector_list`, `collector_delete` (own jobs only). `data_query`, `dashboard_*` and
  `alert_*` are unchanged. A dashboard or alert rule may reference a dataset that a pending job declares, so the
  model can finish its work before approval.

### SSH command guard

Every command (ad-hoc or in a job) passes `SshCommandGuard` before it reaches a server:

- Split into pipeline stages on `|`. Outside single quotes, reject `;`, `&`, `>`, `<`, `(`, `)`, `{`, `}`,
  backslash, backtick, `$`, newlines and `||`. At most 6 stages, 2,000 characters.
- The first stage must start with an allowlist entry (a token prefix such as `df`, `docker ps`, `kubectl get`).
  An entry ending in `/` (`cat /proc/`) restricts the command's path arguments to that prefix, no `..`.
- Later stages may only be filters (`grep head tail sort uniq cut tr wc`), with no path-like arguments and none of
  `-r -R -f -o --recursive --file --output -T --files0-from`, so a filter can read stdin but not files.
- `kubectl` is refused any argument containing `secret`. The default allowlist has no `sudo`, shells,
  interpreters, `awk`/`sed` (a quoted program can run code or write files), `find`, `xargs`, `tee`, `docker
  exec/run/inspect`, or anything that writes. Editing the allowlist is the user's choice.
- The guard is the first line of defense; for a hard guarantee also restrict the SSH key on the server (a read-only
  user, or `restrict` in `authorized_keys`).

### Safety and cost summary

The model's reach is bounded by the allowlist, the approval step, the per-command timeout and output cap, and the
SSH host-key pinning from step 2. Runtime cost is zero model calls; a model run (on its schedule or by hand) is only
needed to create or change jobs, dashboards and rules.

### v2 implementation notes

Implemented as designed above, with these specifics:

- **Code**: `SshCommandGuard` / `SshGuardProvider` (the allowlist, read live from the ssh tool's `AllowedCommands`
  setting), `CollectorSpecs` / `CollectorParser` (declarative parsers), `CollectionJob` + `CollectionJobService`
  (validation, approval rules, built-in template), `CollectionRunner` (runs due jobs, dry-run `TestAsync`, ad-hoc
  `RunCommandAsync`, alert evaluation after each pass, retention pruning), `CollectionHostedService` (the loop).
- **Superseded**: the `health_discover` / `health_report` tools, the per-agent discovery-refresh setting and
  `SshServer.LastDiscoveryAt`, and the Settings > Targets page (it redirects to Settings > Tools, where the SSH tool
  card holds the allowlist and the servers). `HealthScripts` / `HealthParser` / `HealthDatasets` live on as the
  built-in template. A failing discovery job no longer marks a healthy server unreachable: only the built-in
  *report* job writes `reachable=false`.
- **Approval**: new model jobs (and model edits that change the command, parser, dataset or servers) are
  `pending_approval` while the agent's `requireJobApproval` is on (default). Only the user can approve; approving
  re-checks the command against the current allowlist; enabling cannot bypass approval; a model can only edit or
  delete jobs it wrote.
- **Safety at run time**: a job's command is re-checked against the allowlist every time it runs, so tightening the
  list stops jobs that no longer qualify. A builtin job runs only its own known script, never its stored request.
- **Model tools** (always available to Dashboard & Alerts agents, hidden from chat and the skill picker via
  `ITool.RequiresAgentRun`): `ssh`, `collector_test`, `collector_save` (validates, dry-runs on a real server, then
  saves), `collector_list`, `collector_delete`, plus `data_query`, `dashboard_*` and `alert_*`. Dashboards and alert
  rules can reference datasets a still-pending job declares.
- **Verified** against a real Ubuntu host over SSH: exploring with `ssh`, guard refusals with actionable messages, a
  parser that doesn't fit the real output refused with the start of that output, jobs saved pending, template jobs
  running while pending ones don't, approval, then keyed rows stored (`target` and `entity` added, entities keyed by
  server plus `keyFields`).
- **Not yet**: a SQL tool (the job shape and runner are ready for it), and creating custom jobs from the UI (they come
  from the model; the user approves, pauses, runs and deletes them).

## Generic data sources (v3)

v2 still assumed SSH in one place: the agent's settings held `targetGroupIds`. v3 removes that, so any tool that reads
data (SSH today, SQL or an HTTP API later) plugs in the same way and an agent can mix them.

### Model

- **A data tool owns its connections** (SSH: servers in groups; SQL: databases) and its own settings page. It
  implements one small contract, `IDataSource`: list the **scope items** it offers, resolve a scope to concrete
  connections, check a request (SSH: the command allowlist; SQL later: SELECT-only) and run it on one connection.
- **A scope item is a group or a single connection**, whichever the tool offers. For SSH it is a group, and a group
  means its current and future members. A tool with no groups offers each connection as its own item.
- **The agent's scope is per tool:** `toolScopes: { "ssh": [<scope item ids>], "sql": [...] }`. A tool with no entry is
  simply not available to that agent: no data tool is required. The scope is the security boundary: `ssh`,
  `collector_test`, jobs and `data_query` only ever reach connections inside it.
- **Jobs name their tool** (`tool: "ssh"`, the default) and run on that tool's connections within the agent's scope,
  optionally narrowed by server name. Rows still get `target` = the connection's name.
- **Tools offered to the agent** are its type's generic tools (`collector_*`, `data_query`, `dashboard_*`, `alert_*`)
  plus one tool per data source in its scope. Nothing hardcodes `ssh`.
- **UI:** the agent form shows a "Data sources" section built from `GET /api/data-sources`: for each tool, its scope items
  with the connections inside, plus a link to that tool's settings. Selecting items enables the tool for the agent.
- Existing configs with `targetGroupIds` are read as `toolScopes.ssh`.

### v3 implementation notes

- **Contract**: `IDataSource` (`ToolName`, `DisplayName`, `RequestNoun`, `ConnectionNoun`, `ListScopeItemsAsync`, `ResolveAsync`, `Check`,
  `RunAsync`). `DataSourceRegistry` (scoped) finds a source by tool name and resolves a tool's connections within an
  agent's scope. `SshDataSource` is the first implementation (scope items are server groups; it also records each
  server's status and pins its host key). Adding a source means implementing the contract, registering it in
  `Program.cs`, and adding its tool; the runner, job service, collector tools and picker need no change.
- **Agent settings**: `toolScopes: { "<tool>": [scope item ids] }`; a tool with nothing selected is not available to the
  agent. The scope is the boundary for `ssh`, `collector_test`, jobs and `data_query`. `targetGroupIds` from earlier
  configs is read as `toolScopes.ssh` and dropped on the next save.
- **Jobs** carry their `tool` (default `ssh`). Validation uses that tool's own guard and requires the tool to be in the
  agent's scope; a model changing a job's tool sends it back for approval. Rows keep `target` = the connection's name.
- **Tools**: a Dashboard & Alerts agent gets the generic tools (`collector_*`, `data_query`, `dashboard_*`, `alert_*`)
  plus one per scoped data source (`IAgentTypeDescriptor.ToolsFor`); nothing hardcodes `ssh`.
- **UI**: the agent form's Data sources section is built from `GET /api/data-sources` with no per-tool code.
- **Tested** with a second, differently shaped fake source (no groups, query guard) mixed with SSH in one agent.

## Second review hardening

Changes made after a second code review of v2/v3:

- **Guard.** Literal allowlist entries (`cat /etc/hostname`) match exactly, with no extra arguments. Path-prefix entries
  (`cat /proc/`) refuse `..`, globs and process-internal segments (`root cwd exe fd environ mem ...`), which closed a
  symlink route out of `/proc`. Long options are matched by unambiguous abbreviation, so `--compress-program`,
  `--exclude-from`, kubectl `--kubeconfig/--token/-f` and docker `-H/--config` cannot slip through as prefixes. Filter
  pattern operands (grep/tr) may start with `.` `/` `~`, and numeric operands such as `tail -n +2` are accepted.
- **Freshness follows job intervals.** Dashboards and alerts treat a dataset as stale after 3x the longest interval of
  the jobs that feed it (never less than one hour), so a daily job no longer reads as empty.
- **Runner resilience.** A failure on one server or while storing rows is recorded on that job and no longer aborts the
  pass; `LastRunAt` is always written. A command that connects but fails or times out no longer marks the server
  unreachable (only a failed connection does). A command that prints nothing with exit 0 is a valid zero-row result.
- **Scopes.** `toolScopes` keys only enable tools that are in the type's required tools or a registered data source;
  `null` scopes and duplicate ids are tolerated.
- **Approval is bound to content.** Each job exposes an `approvalToken` (hash of its command, parser, dataset and
  servers). `POST .../approve` requires `{ "token": "..." }` and answers 409 if the job changed since it was reviewed.

## Generic collection: nothing built in

The built-in "Linux host health" template is gone, along with `HealthScripts`, `HealthParser`, `HealthDatasets`,
`BuiltinParsers`, the `builtin` parser type and the `POST .../jobs/template/linux-health` endpoint. The platform now
knows nothing about Linux, CPU or disks: the model designs every job, and the dashboard and alerts read whatever
datasets those jobs write. (The older sections above describe the first versions and are kept as history.)

- **`data_source_status`** is the one dataset the platform writes itself. After every pass that ran a job on a
  connection it records one point per connection: `available` (bool), `tool`, `target` and, when it is false, `error`.
  Only a failure to connect counts as unavailable; a command that fails on a connection that answered does not. It works
  for any tool (a server that is down, a database that refuses logins), and it is always declared, so an alert rule can
  be created before the first run: `data_source_status.available eq 0`.
- **Commands outside the allowed list ask permission.** The guard now separates two kinds of refusal. *Hard* refusals
  (chaining, redirects, substitution, newlines, `kubectl` secrets, flags that read files or reach other systems) can
  never be approved. A command that is merely *not on the list* (a binary that is not listed, a path outside an entry's
  prefix, a pipe stage that is not a filter) can still be saved as a job: it always waits for the user, even when the
  agent's `requireJobApproval` is off, and is flagged `outsideAllowlist`. The approval card shows a warning and the
  confirm dialog repeats it. Approving runs exactly that text (the flag is part of the approval token, and the runner
  still refuses anything the tool rejects outright). Editing the command resets the flag and sends the job back for
  approval. Such a command cannot be dry-run or run ad hoc first, so the model is told to save it and read the results
  once it runs (the run page's follow-up chat is a good place to ask it to look).
- **Existing data.** A migration deletes the old `builtin` jobs, their five datasets (`system`, `disk`, `temperature`,
  `container`, `discovery`) for those agents, and the alert rules that read them. Dashboards are left alone: ask the
  agent (run it, or use the chat on a run) to rebuild them against what it collects next.

### Example prompt: a Linux server

The agent's prompt says what to monitor; this is a starting point that works with the default allowed commands:

> Monitor every server: CPU usage and load, memory and swap, disk usage per mount, temperatures if sensors exist, and
> docker containers if docker is installed. Use `cat /proc/stat`, `cat /proc/meminfo`, `df -P -B1`, `sensors` and
> `docker ps -a --format '{{json .}}'`. Collect every 5 minutes. Build one dashboard: server availability first, then
> headline CPU, memory and disk gauges, then time series, then a container table. Alert on: a server unavailable
> (data_source_status), CPU over 90% for 10 minutes, memory over 90%, any disk over 90%, and any container not running.

## Naming: SSH servers

The SSH machines were first called "targets", which collided with the generic `target` field on every collected row
(the connection's name, whatever the tool). They are now `SshServer` / `SshServerGroup`:

- Entities `SshServer`, `SshServerGroup`; tables `SshServers`, `SshServerGroups`; `ISshServersRepository`,
  `SshServersRepository`, `SshServersService`, `SshServersController`; the code lives in `Services/Ssh`
  (namespace `PersonalAI.Core.Services.Ssh`).
- Routes: `/api/ssh/servers` and `/api/ssh/server-groups` (were `/api/targets`, `/api/target-groups`). The group
  list's `targetCount` is `serverCount`.
- Frontend: `sshServers` Redux slice, `useSshServers`, `components/sshServers/`; the SSH tool's settings field is
  `Servers` (custom component key `sshServers`).
- Unchanged on purpose, because they are generic and not SSH-specific: the `target` field on rows, alert rules and
  widget queries, a job's `targets` (connection names to run on), `DataConnection`, and the legacy
  `targetGroupIds` agent-config key that is still read as the SSH scope.
- The `RenameTargetsToSshServers` migration moves the existing rows into the new tables (it does not drop them) and
  can be reverted the same way.

## Generic wording and contract

The generic layers (collection runner, job service, tools the model reads, agent preamble, alert and dashboard
validation, the agent form and jobs panel) no longer assume SSH or servers:

- A job's `command` field is now `request` (a command for `ssh`, a query for a database tool), in the job tools' JSON, the
  jobs API and the UI. The `ssh` exploration tool keeps its own `command` argument because it is SSH-specific.
- A job that names no `tool` uses the agent's only data tool; an agent with several must say which, and one with none
  cannot have jobs yet. There is no default of `ssh` any more.
- Messages say "connection" (server, database...) and "request"; the tool's own `RequestNoun` ("command", "query") is used
  where a message is about one tool. The approval dialog and cards name the job's tool.
- Examples that are Linux-specific (CPU, disk) remain only as examples inside the SSH-specific guidance.

## Third review hardening

- **Listed commands can no longer be turned into mutations.** A prefix entry used to accept any further arguments. Now
  `date` (`-s`, `-f`, a bare operand that sets the clock), `hostname` (any operand, `-F`, `-b`), `ip` (unknown options such
  as `-b`/`-n`, mutating words like `set add del flush exec`, the `netns` object), `ss` (`-K`, `-F`, `-D`), `sensors`
  (`-s`, `-c`) and `kubectl cluster-info dump` are "not on the list": refused unless the user approves the exact text.
  `journalctl` also refuses `--vacuum-*`, `--rotate`, `--flush`, `--sync`, `--cursor-file`, `--setup-keys`, `--verify` and
  `-M/--machine`, and `systemctl` refuses `-H/--host` and `-M/--machine` (hard refusals).
- **Filters never read files.** A `grep` whose pattern comes from a flag (`-e`, `--regexp`) takes no operand, so
  `ps | grep -eX notes.txt` is refused. Value-taking flags (`cut -d ' ' -f 1,3`, `sort -k 2,2`, `head -n 5`, `uniq -f 1`)
  are understood, so common pipelines are no longer refused as having "file arguments".
- **The "Default job interval" setting is applied** to a job that omits `intervalMinutes`.
- **A follow-up chat does not rewrite the run it continues.** The run keeps its original status, summary and completion
  time; a failed or cancelled follow-up is recorded as an error entry in the transcript instead of turning a succeeded
  run into a failed one. Only one follow-up per run can be active (a lock, not just a status check).
- **Retention pruning works in batches** (5,000 rows per transaction) instead of loading every expired row.
- **The time-series chart measures itself when it first has data**, so a widget that starts empty is no longer stuck at its
  default width.
