# API Endpoints

All endpoints require bearer token auth (except `/auth/*` and `/api/sandbox/host`) when `Auth:Enabled`.

## Chat Sessions

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/sessions?search=&page=1&pageSize=20` | One page of sessions (ordered by `UpdatedAt` desc), optionally filtered by case-insensitive title substring → `{ items, totalCount, page, pageSize }` (`pageSize` capped at 100) |
| `POST` | `/api/sessions` | Create a new session |
| `GET` | `/api/sessions/{id}` | Get session detail with messages and pending approvals |
| `PATCH` | `/api/sessions/{id}/title` | Rename a session (`{ title }`, trimmed, 1–200 chars) → session summary; `400` if empty/too long |
| `PATCH` | `/api/sessions/{id}/settings` | Update session settings (tools, context size, thinking, reasoning effort, etc.). Optional `providerKey` + `modelName` switch the chat's model (omit to keep it); `400` on an unknown provider or a provider without a model, `409` while a turn is running |
| `POST` | `/api/sessions/{id}/messages` | Start a turn in the background → **SSE stream** of it; `409` if one is already running |
| `GET` | `/api/sessions/{id}/turn/stream` | Re-attach to the running turn → **SSE** replay + live; `204` if none |
| `POST` | `/api/sessions/{id}/turn/stop` | Stop the running turn (`204`; `404` if none) |
| `POST` | `/api/sessions/{id}/compact` | Summarize the whole conversation so far into the session's history summary; runs as a turn → **SSE** ending in `compacted` (`foldedMessages`) then `done`; `409` if a turn is running or approvals are pending |

## Models

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/models` | List installed models (`?providerKey=`, default `ollama`); each carries its saved per-model `settings` |
| `GET` | `/api/models/settings-fields` | Per-model settings the provider honors (`?providerKey=`) — name, label, type, options, help |
| `PUT` | `/api/models/settings` | Replace one model's settings (`?providerKey=&model=`, body `{ "values": { ... } }`); blank/`auto` values are dropped; `400` on an unknown field or bad value |
| `DELETE` | `/api/models/settings` | Reset one model's settings to auto (`?providerKey=&model=`) → `204` |
| `POST` | `/api/models/pull` | Pull a model → **SSE stream** of download progress (Ollama only) |

## Settings

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/app-settings` | Get global app settings |
| `PATCH` | `/api/app-settings` | Update global app settings |

## Tool Invocations (Approval Flow)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/tool-invocations/{id}/resolve` | Approve/deny a pending tool → **SSE stream** of the continued turn (same background runner; `409` if not pending or a turn is running) |

## AI Providers

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/ai-providers` | List providers with config schema + current values (secrets masked) |
| `PATCH` | `/api/ai-providers/{key}` | Update a provider's config fields |

## Secrets Vault

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/secrets` | List secret keys (values never returned) |
| `POST` | `/api/secrets` | Create a secret (409 if key exists) |
| `PUT` | `/api/secrets/{key}` | Update a secret's value |
| `DELETE` | `/api/secrets/{key}` | Delete a secret |

## Skills

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/skills` | List skills |
| `GET` | `/api/skills/available-tools` | List tool names usable in a skill's allowlist |
| `POST` | `/api/skills` | Create a skill |
| `GET` | `/api/skills/{id}` | Get a skill |
| `PATCH` | `/api/skills/{id}` | Update a skill |
| `DELETE` | `/api/skills/{id}` | Delete a skill |
| `POST` | `/api/skills/{id}/run` | Run a skill → **SSE stream** |

## Agents

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/agents/types` | List agent types (`General`, `DashboardAlerts`) with display name and description |
| `GET` | `/api/agents` | List agents |
| `POST` | `/api/agents` | Create an agent (`type` + type-specific `typeConfig`, validated per type; see [Dashboard Agents](DASHBOARD_AGENTS.md)) |
| `GET` | `/api/agents/{id}` | Get an agent |
| `PATCH` | `/api/agents/{id}` | Update an agent |
| `DELETE` | `/api/agents/{id}` | Delete an agent |
| `POST` | `/api/agents/{id}/run` | Trigger a manual run → **SSE stream** (includes `run-started`, `llm-call`, `token-usage`) |
| `GET` | `/api/agents/{id}/runs` | List an agent's run history (each with `llmCallCount`, `totalInputTokens`, `totalOutputTokens`) |
| `GET` | `/api/agents/{id}/runs/{runId}` | Full transcript for one run, plus the same counters |
| `POST` | `/api/agents/{id}/runs/{runId}/messages` | Chat on a finished run. Body `{ "message": "..." }`; streams the agent's reply as SSE. The run's transcript is the conversation so far, the agent keeps its tools (so it can add a widget or a job), and the exchange is appended to the same run without touching the schedule. 400 for an empty message, 404 for an unknown run, 409 while the run is still running |

## Tools

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/tools` | List tools with their settings schema, current values, and `IsConnected` |
| `PATCH` | `/api/tools/{name}` | Update a tool's settings fields |
| `GET` | `/api/tools/gmail/oauth/redirect-uri` | The configured Gmail OAuth redirect URI |
| `GET` | `/api/tools/gmail/oauth/authorize-url` | Google OAuth2 authorize URL to redirect the user to |
| `GET` | `/api/tools/gmail/oauth/callback` | Google's OAuth2 redirect target — stores the refresh token |

## Sandbox

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/sandbox/host` | **WebSocket** upgrade `PersonalAI.SandboxHost` dials into; host-key authenticated (`X-Sandbox-Host-Key`), `[AllowAnonymous]` since the broker has no user bearer token |
| `GET` | `/api/sandbox/status` | `{ connected, hostId, connectedAt, activeSandboxes }` for the `/settings/tools` broker indicator |
| `GET` | `/api/sandbox/{ownerId}/status` | `{ canBrowse, status, ports }` for a `ChatSession`/`AgentRun` id — gates the frontend's "Sandbox files" button; a pure read, never creates or recreates a container |
| `GET` | `/api/sandbox/{ownerId}/files` | List a directory (`?path=&recursive=`) in that owner's sandbox |
| `GET` | `/api/sandbox/{ownerId}/files/content` | Read a file's content (`?path=`); `{ isText, content, truncated, totalSize }` — binary files get `isText: false` and no content |
| `PUT` | `/api/sandbox/{ownerId}/files/content` | Full overwrite of a file's content from the browser's editor |
| `GET` | `/api/sandbox/{ownerId}/files/download` | Download one file (`?path=`), byte-exact — never through the model-facing `Read` op's lossy text decoding |
| `POST` | `/api/sandbox/{ownerId}/files/zip` | Download a zip of `{ paths }` (or everything, if omitted) |
| `POST` | `/api/sandbox/{ownerId}/files/upload` | Multipart upload of one file into a target directory |
| `POST` | `/api/sandbox/{ownerId}/files/mkdir` | Create a directory |
| `POST` | `/api/sandbox/{ownerId}/files/delete` | Delete a file or directory |
| `POST` | `/api/sandbox/{ownerId}/files/move` | Move/rename a file or directory |

## Authentication

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/auth/status` | Whether auth is enabled |
| `GET` | `/api/auth/login` | Redirect to the OAuth2 provider (includes PKCE challenge) |
| `POST` | `/api/auth/exchange` | Exchange an authorization code for tokens |
| `POST` | `/api/auth/refresh` | Refresh an access token |
| `POST` | `/api/auth/logout` | Revoke a refresh token |
| `GET` | `/api/auth/me` | Current subject/scope from the validated bearer token |

---

See [Concepts: SSE Streaming Protocol](CONCEPTS.md#sse-streaming-protocol) for the event types emitted by streaming endpoints.

## SSH servers

| Method | Path | Description |
|--------|------|-------------|
| `GET` / `POST` | `/api/ssh/server-groups` | List / create server groups (list includes `serverCount`) |
| `PATCH` / `DELETE` | `/api/ssh/server-groups/{id}` | Rename / delete a group (delete also removes its servers) |
| `GET` / `POST` | `/api/ssh/servers` | List (optional `?groupId=`) / create SSH servers; `secretRef` must name an existing secret |
| `GET` / `PATCH` / `DELETE` | `/api/ssh/servers/{id}` | Get / update / delete a server |
| `POST` | `/api/ssh/servers/{id}/test-connection` | Runs a probe over SSH → `{ success, serverInfo, error, hostKeyFingerprint }`; pins the host key on first success |
| `POST` | `/api/ssh/servers/{id}/reset-host-key` | Clears the pinned host key |

## Agent data (Dashboard & Alerts agents)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/agents/{id}/datasets` | Datasets with kind, field description, point count, last update |
| `GET` | `/api/agents/{id}/datasets/{name}/points` | Points oldest-first; query `entity`, `sinceMinutes`, `latest=true`, `limit` |
| `POST` | `/api/agents/{id}/collect-now` | Run every active collection job now → `{ jobsRun, results: [{ job, target, success, rows, error }] }`; 409 if a pass is already running |

## Dashboards (Dashboard & Alerts agents)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/agents/{id}/dashboard` | The current spec, its version and reason (`?version=` for an old one); 204 if the agent has no dashboard yet |
| `GET` | `/api/agents/{id}/dashboard/data` | Live data for every widget, keyed by widget id (`?version=` to render an old version) |
| `GET` | `/api/agents/{id}/dashboard/versions` | Saved versions, newest first |
| `POST` | `/api/agents/{id}/dashboard/versions/{n}/restore` | Saves version n as a new current version |

## Alerts (Dashboard & Alerts agents)

| Method | Path | Description |
|--------|------|-------------|
| `GET` / `POST` | `/api/agents/{id}/alert-rules` | List / create alert rules (`name, dataset, field, operator, threshold, forMinutes, severity, target, filter, enabled`); validated against collected data |
| `PATCH` / `DELETE` | `/api/agents/{id}/alert-rules/{ruleId}` | Update / delete a rule (disabling resolves its open alerts; deleting removes its history) |
| `GET` | `/api/alerts` | Alerts across agents: `?state=active` (default, firing), `resolved` (last 7 days) or `all`; `?agentId=` to narrow. Each alert has `acknowledged` and `acknowledgedAt` |
| `GET` | `/api/alerts/summary` | `{ firing, critical, warning, unacknowledged, unacknowledgedCritical, acknowledged }`; the nav badge shows `unacknowledged` (what still needs attention) |
| `POST` | `/api/alerts/{id}/acknowledge` | Mark an alert as seen. It stays listed while it fires and resolves on its own when the condition clears; if it comes back it is a new, unacknowledged alert |
| `POST` | `/api/alerts/{id}/unacknowledge` | Take an acknowledgement back |
| `POST` | `/api/alerts/acknowledge-all` | Acknowledge every unacknowledged firing alert (`?agentId=` to narrow) → `{ acknowledged }` |

## Collection jobs (Dashboard & Alerts agents)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/agents/{id}/jobs` | List the agent's jobs: `tool`, `request`, parser, dataset, interval, `status` (`pending_approval`, `active`, `disabled`), `source` (`user`, `llm`), `outsideAllowlist` (the command is not on the tool's allowed list and runs only because it was approved as written), `approvalToken`, last run |
| `POST` | `/api/agents/{id}/jobs/{jobId}/approve` | Approve a pending job; body `{ "token": <job.approvalToken> }` (400 without it; 409 if the job changed since review, isn't pending, or its request is no longer allowed) |
| `POST` | `/api/agents/{id}/jobs/{jobId}/enable`, `.../disable` | Resume / pause an approved job (409 for a pending one: approve or delete it) |
| `POST` | `/api/agents/{id}/jobs/{jobId}/run` | Run one active job now |
| `DELETE` | `/api/agents/{id}/jobs/{jobId}` | Delete a job (collected data is kept) |

## Data sources

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/data-sources` | The data tools an agent can be scoped to, each with its scope items (a group of connections, or a single connection) and the connection names inside. Drives the Data sources picker; an agent's selection is stored as `typeConfig.toolScopes` (`{ "<tool>": [scope item ids] }`) |
