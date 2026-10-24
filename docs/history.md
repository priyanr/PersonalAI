# History

Append-only log of what was done and why, newest entry first.

### Simplify the agent loops and the largest files (rework, 2026-10-24)
- What: ChatAgentService split: compaction moved to ChatHistoryCompaction; one refusal path and one result recorder in
  the tool loop. Shared ProgressStream (run work, stream its progress) and SkillExecutionService.RunAsToolAsync replace
  three copies each across the chat, skill and agent loops. The agent's retrying model call is a plain method.
  SandboxTool handlers take one Sandbox object; OpenAiCompatibleProviderBase split by protocol; long comments trimmed.
- Why: user request: code simple to read for humans.
- Touched: Services/Agent, Services/Agents, Services/Skills, Tools/SandboxTool, OpenAiCompatible provider. No behaviour,
  prompt or API change, except that an invalid-JSON error on an approved tool call now uses the longer chat wording, and
  a cancelled retry wait in an agent run now marks the run failed instead of leaving it Running.
- Verified: build, 1078 tests pass.
- Open: Dashboard & Alerts collection code left as is (long but already sectioned); Async suffix naming is mixed.

### Rework to the coding rules (rework, 2026-10-24)
- What: Backend reworked to `.claude/rules/coding-guideline.md`. Single-implementation service interfaces removed;
  repository interfaces live next to their class; repositories only read and every write moved into a feature
  service (new: SecretsService, SkillsService, AgentsService, SshServersService, AppSettingsService, ToolConfigService,
  AIProviderConfigService, ChatSessionsService, DatasetService, AlertsService); controllers call one service method per
  action; one `ApiExceptionHandler` maps service exceptions to `{ message }`; SecretsCache removed; expression-bodied
  methods converted to block bodies; test-only optional constructor parameters made required.
- Why: user request: simpler, less enterprise code that follows the new rules.
- Touched: src/PersonalAI.Core, src/PersonalAI.Api, tests. Existing features affected (no endpoint, JSON field, DB or
  prompt change): error bodies of Sessions and ToolInvocations are now `{ message }` instead of a bare string; 404s that
  had an empty body now carry `{ message }`; "missing required field(s)" on tool and provider settings names the
  fields in the message instead of a separate `fields` array (the UI never read it); create/update skill and agent
  share one request record each (same JSON).
- Verified: build, 1078 tests pass, API smoke test on a scratch database (GET/POST/PATCH/DELETE for secrets, skills,
  agents, SSH groups, sessions, dashboards, jobs, alerts).
- Open: see `docs/status.md`.
