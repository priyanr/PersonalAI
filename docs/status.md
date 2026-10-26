# Status

Current snapshot. Rewritten in place; the reasons behind past changes are in `history.md`.

## Done (rework branch, 2026-10-24)
- Coding-rules rework of Core and Api: concrete services, read-only repositories, writes in services, thin
  controllers, one exception handler, block-bodied methods. See `history.md`.

## Verified
- `dotnet build PersonalAI.slnx` clean (only the pre-existing warnings), `dotnet test`: 1081 passed, 3 skipped.
- API smoke test against a scratch SQLite database: CRUD and error paths return the same JSON as before.

## Open
- Not exercised against a live LLM, sandbox broker or SSH host after the rework (chat turn with tools, agent run,
  collection pass, sandbox file browser).
- Controllers that still map errors themselves: AuthController (OAuth login/refresh/logout), GmailOAuthController
  (browser redirect callback), SandboxFilesController (broker disconnect/timeout to 503), SandboxHostController.
- Naming conflict with the rules, kept for consistency: request records are named `*Request`, not `*Dto`.
- Still long but sectioned: CollectionRunner, CollectionJobService, NetworkScanTool. Method names mix `Get` and `GetAsync`.
- Unused DTO `AgentScheduleFields` (pre-existing dead code, left in place).
- Code-review cleanups not done yet: tool-call execution written three times (chat, agent, skill loops);
  `EnsureDashboardAgent` and `Clean()` each copied in three services; run-accepts-message check duplicated in
  AgentsService and AgentExecutionService; controllers pass CancellationToken to plain CRUD calls; one-caller private
  methods `CallModelAsync` and `WakeHostAsync`; `SshGuardProvider.Current` rebuilds the guard on each read.

## Next steps
- Run the app with a real provider and walk through chat, skills, an agent run and a Dashboard & Alerts agent.
- Decide whether to move the OAuth and sandbox-file error mapping into services as well.
