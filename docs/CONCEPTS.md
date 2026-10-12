# Core Concepts

## Pluggable AI Providers

`IAIProvider` (`Core/Interfaces/IAIProvider.cs`) abstracts the LLM backend: `Key`/`DisplayName`,
declarative `ConfigFields` (rendered generically by the `/settings/providers` UI), streaming
`ChatStreamAsync` (text + tool calls), `ListModelsAsync`, and `PullModelStreamAsync` (hosted providers
like OpenAI/DeepSeek throw `NotSupportedException` — pulling only makes sense for Ollama).

- **`OllamaProvider`** — talks to Ollama's native `/api/chat`, `/api/tags`, `/api/pull`.
- **`OpenAiCompatibleProviderBase`** — the OpenAI Chat Completions SSE protocol, including reassembly of
  OpenAI's fragmented streaming tool-call deltas into complete tool calls. `OpenAiProvider` and
  `DeepSeekProvider` are thin subclasses (DeepSeek's API is OpenAI-compatible).
- **`AIProviderRegistry`** resolves a provider by `Key` ("ollama", "openai", "deepseek").
- Each provider's config (base URL, API key, ...) lives in the `AIProviderConfig` table, cached
  in-memory by `AIProviderConfigCache`, and edited via `/api/ai-providers` (`GET`/`PATCH`) — secrets are
  never echoed back, only a `HasValue` flag, and a blank value in a PATCH leaves the stored value intact.
- Per-model capability settings (output-limit parameter, max output tokens, tool calling, reasoning effort,
  thinking) live in `ModelConfig`, are cached in the same `AIProviderConfigCache`, and are applied inside the
  providers — so chat, skills, agents and compaction all honor them. See CONFIGURATION.md → Per-model settings.
- `ChatSession`, `Skill`, and `Agent` each carry their own `ProviderKey` + `ModelName` + `NumCtx`, so
  different sessions/skills/agents can target different providers/models simultaneously.

## Agentic Loop (ChatAgentService)

The heart of chat. Each user message triggers an iterative loop:

1. **User message** is persisted to the DB.
2. Full conversation history (with optional trimming) is sent to the session's `IAIProvider` with tool
   definitions.
3. The model's response is **streamed via SSE** back to the browser in real-time.
4. If the model emits **tool calls**, each tool is executed:
   - **Normally** → executed immediately, result appended to conversation.
   - **Needs approval** (a file op outside the workspace; a sandbox command matching a dangerous-command
     pattern) → returns `ToolApprovalRequired`, session enters `AwaitingApproval` status, agent pauses.
5. After all tool results are collected, **loop back to step 2** (the model sees the tool results).
6. Loop exits when the model responds with **plain text** (no tool calls), hits the **iteration cap** (`Agent:MaxIterations`, default 25), or is **paused for approval**.

### Key Design Decisions

- **Stateless HTTP**: every entry point reloads conversation state from the DB. A tool-approval pause is "stop and persist", resumable by a separate HTTP request.
- **Turns run in the background, not on the request**: `ChatTurnRunner` (singleton, `IChatTurnRunner`) owns
  every running turn — one per session, each on its own DI scope — and the SSE response merely subscribes.
  Closing the tab or reloading only detaches the viewer; a reloaded page sees `isTurnRunning` on the session
  and re-attaches via `GET /api/sessions/{id}/turn/stream`, which replays the turn so far (tokens coalesced)
  then continues live. A second send while a turn runs is `409`. The **Stop** button
  (`POST /api/sessions/{id}/turn/stop`) cancels it; a stopped, failed, or shutdown-interrupted turn is
  finalized on a fresh scope (`FinalizeInterruptedTurnAsync`): unanswered tool calls get an "interrupted"
  result, streamed-but-unsaved assistant text is kept, and a System notice records why (not for failures).
  Running state is in-memory only, so a crash can't leave a session stuck "busy". A cancelled tool is
  abandoned, not rolled back — e.g. a sandbox `exec` keeps running in its container until its own timeout.
- **Empty / truncated replies are surfaced, not swallowed** (chat, skill and agent loops alike, via
  `ModelReplyDiagnostics`): a reply with no text and no tool call (typically a reasoning model that spent its
  whole `MaxOutputTokens` thinking and hit `finish_reason: "length"`) is never treated as a finished answer.
  Chat ends the turn with a System notice + `error` event naming the cause and fix (raise the provider's
  "Max output tokens"; "turn thinking off" is only suggested where `IAIProvider.SupportsThinkingToggle`);
  a skill emits an `error`; an agent run is recorded `Failed`. A text answer cut off at the limit keeps its
  partial text plus a notice. A tool call cut off mid-arguments, or right after its name, is not run — the
  model gets an error result telling it to split the call. Chat notices are stored with
  `ChatMessage.ExcludeFromContext`, so they show in the transcript but are never sent to the model or summarized.
- **Thinking preference**: `ChatStreamAsync`'s `think` is `bool?` — a chat session passes its own on/off,
  skill/agent runs and compaction pass `null` (provider default; Ollama maps it to off). DeepSeek sends
  `thinking: {type: "disabled"}` only for an explicit `false`, and gets each tool call's `reasoning_content`
  back for the rest of that turn (held in memory, never persisted).
- **Duplicate call prevention**: if the model re-emits an identical tool call that already failed, it's short-circuited with the previous error.
- **Unfinished announcement nudge**: if the model's reply ends with `:` (looks like it was about to act but forgot the tool call), a system nudge is injected once.
- **History compaction**: when enabled (`ChatSession.TrimHistoryEnabled`), tool results are capped and
  history older than a kept window is folded into a running per-session summary — via a side LLM
  call, batched and dual-triggered by pending message count or char count — instead of being dropped,
  so long sessions stay coherent without overflowing the model's context. Falls back to the old
  drop-with-note behavior for a turn if the summarization call itself fails. Full design:
  **[`COMPACTION_ARCHITECTURE.md`](COMPACTION_ARCHITECTURE.md)**.

## Skills & Agents: unattended agentic loops outside chat

Two more entities reuse the same "agentic loop" shape as `ChatAgentService`, but run independently of any
`ChatSession` and persist nothing to the DB mid-run (only the final result, for Agents):

- **Skill** (`Skill` entity) — a reusable prompt plus an **allowlist** of tools it may call
  (`EnabledToolsJson`) and secrets it may read (`SecretKeysJson`). `SkillExecutionService` runs it through
  its own short tool-call loop via `AIProviderRegistry` + `ToolRegistry`. A tool call outside the
  allowlist is rejected before it reaches the tool; a tool call that would need user approval is rejected
  with an explanatory result instead of pausing, since there's no user to approve it. Run via
  `POST /api/skills/{id}/run` (SSE, no approval events).
  - **Secret substitution**: the model never sees a raw secret value. It's told to reference a declared
    secret as `{{secret.KEY}}` inside tool call arguments; `SecretSubstitution` swaps in the real value
    immediately before the tool executes, so only the placeholder ever appears in conversation history.
- **Agent** (`Agent` entity) — its own prompt orchestrating a loop whose "tools" are the user's **Skills**
  (each exposed as a tool named `skill_<slug>`, invoked via `ISkillExecutionService` and folded back in as
  one tool result — a loop nested inside a loop). Runs on demand (`POST /api/agents/{id}/run`, SSE) or on
  a schedule: `ScheduleKind` is `Manual` / `Once` / `Interval` / `Cron`, computed into a `NextRunAt` so the
  scheduler's poll is a cheap lookup. `AgentSchedulerHostedService` polls due agents every
  `Scheduler:PollIntervalSeconds` (default 30s) and fires them (`AgentRunTrigger.Scheduled`); each run —
  manual or scheduled — is recorded as an `AgentRun` (status, summary, a transcript of assistant text and
  each skill invocation) via `GET /api/agents/{id}/runs` / `/runs/{runId}`.
- Both services use a manual async-enumerator pump around provider/skill calls (a C# iterator can't `yield`
  inside `try/catch`), so a provider exception always finalizes cleanly (Skill: an error tool-result /
  stream error; Agent: `AgentRun.Status = Failed`) instead of crashing the loop or, for scheduled runs,
  the scheduler's tick.

## Secrets Vault

A general-purpose key/value store (`Secret` entity: `Key`, `Value`, `UpdatedAt`), separate from the
per-provider `AIProviderConfig` table, for Skills to read via `SecretKeysJson`. `SecretsCache` mirrors the
DB in memory. `/api/secrets` never echoes a stored value back — only a `HasValue` flag — and a duplicate
`Key` on create returns `409`. Same plaintext-in-local-SQLite trust model as `AIProviderConfig` and
`ToolConfig` — not a hardened vault.

## Tool System

Tools implement the `ITool` interface:

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    string ParametersSchemaJson { get; }  // JSON Schema for tools[].function.parameters
    IReadOnlyList<ToolSettingField> SettingsFields => [];        // opt-in per-tool config (e.g. OAuth)
    bool IsConnected(ToolConfigCache cache) => false;            // opt-in "connected" status
    Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct);
}
```

A tool returns either:
- `ToolCompleted(content, isError)` — result is immediate.
- `ToolApprovalRequired(reason, resolvedPath)` — requires user approval (a file op outside the workspace;
  a sandbox `exec` command matching a dangerous-command pattern — see [Sandbox](SANDBOX_ARCHITECTURE.md)).

### Built-in Tools (11)

| Tool | Description | Approval? | Per-session toggle |
|------|-------------|-----------|---------------------|
| `web_search` | Search the web via SearXNG | Never | `WebSearchEnabled` |
| `read_file` | Read file contents (with offset/limit) | If outside workspace | `ReadFileEnabled` |
| `write_file` | Create or overwrite a file | If outside workspace | `WriteFileEnabled` |
| `edit_file` | Find-and-replace exact string in a file | If outside workspace | `EditFileEnabled` |
| `list_files` | Recursively list directory contents | If outside workspace | `ListFilesEnabled` |
| `search_files` | Regex search across file contents | If outside workspace | `SearchFilesEnabled` |
| `network_scan` | ICMP/TCP/UDP/HTTPS LAN reconnaissance | Never (scope-restricted to private IPs) | `NetworkScanEnabled` (default **off**) |
| `http_request` | Arbitrary HTTP calls against an allow-listed URL pattern (`HttpTool:AllowedUrls`) | Never | `HttpRequestEnabled` |
| `gmail` | Send/search/read Gmail via OAuth2; declares `SettingsFields` (client id/secret) and `IsConnected` (has a stored refresh token) | Never | `GmailEnabled` (default **off**) |
| `computer_control` | Whole-desktop mouse/keyboard/screen control (Windows-only, `SendInput`/GDI/UI Automation). `screenshot` saves a PNG under `ToolOutput:RootPath` and returns a text UI Automation element tree (names + bounding boxes) instead of an image, since the chat pipeline is text-only end-to-end; move/click/drag/type/send_keys/get_cursor_position all take explicit coordinates the model reads off that text | Never (self-gated like `network_scan`, not workspace-boundary approval) | `ComputerControlEnabled` (default **off**) |
| `sandbox` | Full Docker container per owner (chat session, or one skill/agent run): `exec`, `read_file`, `write_file`, `edit_file`, `list_files`, `search_files`, `delete`, `move`, `mkdir`, `ports` — everything the in-process file tools offer, arbitrary command execution, and a small set of published ports a server started inside it can bind to. Optional Host start/stop URLs wake or sleep the machine running the Docker host itself. See [Sandbox](SANDBOX_ARCHITECTURE.md) below | If `exec`'s command matches a dangerous-pattern heuristic (package install, `sudo`, `rm -rf`, ...) — same approve/deny flow as a workspace escape, opt-out via `Sandbox:RequireApprovalForDangerousCommands`; otherwise never | `SandboxEnabled` (default **off**) |

### Workspace Sandboxing

The `Workspace` class resolves model-supplied paths against a root directory (`Workspace:RootPath`). Paths inside the root execute without approval; paths outside require explicit user approval via the UI — except a path rooted at `/work` (the sandbox tool's own mount point, never `Workspace:RootPath` in any deployment), which every in-process file tool refuses outright with a pointer to the `sandbox` tool instead of pausing for approval: a model that has used the sandbox and internalized "paths are relative to `/work`" sometimes calls these workspace-confined tools by the same action name with a `/work`-rooted path by mistake, and that path can never legitimately belong here.

### Per-Session Tool Configuration

Each `ChatSession` has `ToolsEnabled` (master switch — when false, no tool definitions are sent at all)
plus the individual toggles in the table above.

### Gmail OAuth flow

`GmailOAuthController` (`/api/tools/gmail/oauth`) issues the Google authorize URL (`authorize-url`),
reports the configured redirect URI (`redirect-uri`), and handles Google's callback (`callback`), storing
the resulting refresh token via `ToolConfigCache`/`GmailOAuthClient`. `GmailConnectButton` in the frontend
drives this from `/settings/tools`. `Gmail:FrontendBaseUrl` controls where the callback redirects back to.

## Delegated Authentication

Login is delegated to an external **standard OAuth2 / OpenID Connect provider** (Authorization Code grant
+ PKCE) rather than a built-in user/password system. Nothing is provider-specific in code: the provider
is chosen purely by `Auth:*` config. Any OIDC provider (Keycloak, Auth0, Okta, Entra ID, ...) works by
pointing `Authority` at it with `UseDiscovery` on; a plain OAuth2 provider without discovery works by
turning `UseDiscovery` off and listing its endpoints explicitly (plus `JwtKey` if it signs JWTs with a
shared HMAC secret) — see `.env.example`. When
`Auth:Enabled` is `false` (the default), nothing changes — the app is fully open.

Pure REST/bearer-token model — **no auth cookies**: the API hands access/refresh/ID tokens back to the
frontend as JSON; the frontend stores them in `localStorage` and sends them as a standard
`Authorization: Bearer <token>` header on every request (`lib/auth.js`, `lib/api.js`, `lib/sse.js`).

- `AuthSettings` (section `Auth`, fully documented in `AuthSettings.cs`): `Enabled`, `Authority`,
  `ClientId`, `ClientSecret`, `RedirectUri` (the **frontend's** own `/auth/callback` route — the provider
  redirects the browser there directly, not through the API), `Scope`, `AuthorizeParameters` (extras such
  as Auth0's `audience`); endpoint resolution `UseDiscovery` / `MetadataAddress` / `RequireHttpsMetadata`
  and explicit `AuthorizationEndpoint` / `TokenEndpoint` / `RevocationEndpoint` / `IntrospectionEndpoint`
  / `EndSessionEndpoint` / `JwksUri` (absolute, or relative to `Authority`; explicit wins over discovery);
  `TokenEndpointAuthMethod` (`client_secret_post` | `client_secret_basic`); token checks `JwtKey`,
  `Issuer`, `Audience`, `ValidateClientId` + `ClientIdClaimTypes`, `IntrospectAccessTokens`,
  `ValidateCacheSeconds`; and `PostLogoutRedirectUri`.
- `OAuthEndpointResolver` (singleton) merges explicit endpoints over the OIDC discovery document (cached
  12h; a failed fetch isn't cached). `OAuthClient` uses only standard endpoints: authorize URL (+ PKCE
  S256 challenge from the browser), code exchange / refresh (RFC 6749 — a refresh response without a new
  `refresh_token` keeps the old one), revocation (RFC 7009), introspection (RFC 7662), and the OIDC
  end-session URL. A 4xx from the provider means "rejected" (→ 401); a 5xx/unreachable provider throws
  `HttpRequestException` (→ 503), so an outage never looks like a logout to the frontend.
- JWT bearer auth is wired by `Api/Auth/AuthServiceCollectionExtensions` only when `Auth:Enabled`.
  Signature keys come from `JwtKey` (shared HMAC key), else the discovery document's JWKS, else
  `JwksUri`; issuer is checked when known (discovery or `Issuer`), audience when `Audience` is set. An
  `OnTokenValidated` check rejects tokens issued to another client (first of `client_id`/`azp`/`cid`/
  `appid` present must equal `ClientId`) — defends against providers that sign every client's tokens with
  one key. Then `TokenValidationCache` introspects the token (when the provider has an introspection
  endpoint), remembering the answer in memory (keyed by token hash, capped at the token's `exp`) for
  `Auth:ValidateCacheSeconds` (default 60). An unreachable provider still fails closed, but as **503**,
  not 401. Startup fails fast on a config that could never verify a signature. A global `AuthorizeFilter`
  then gates every controller by default. Access tokens must be JWTs to authenticate API calls (opaque
  tokens are accepted at login — the subject then comes from the ID token — but can't be verified here).
- `AuthController` (`/api/auth`), all actions `[AllowAnonymous]` except `/me`: `GET /login?state=&code_challenge=`,
  `POST /exchange` (`{ code, codeVerifier }`), `POST /refresh`, `POST /logout` (`{ refreshToken, idToken }`
  → `{ endSessionUrl }`, which the frontend navigates to so the provider's own session ends too),
  `GET /status` (lets the frontend skip the gate when auth is off), `GET /me` (scope from `scope` or `scp`).
- The frontend refreshes single-flight (one shared promise per tab + a Web Lock across tabs, with request
  and lock-wait timeouts), refreshes-and-retries once on a 401, and clears tokens only when the provider
  actually rejects the refresh token — refresh tokens are typically single-use (rotated), so parallel
  refreshes would otherwise log the user out.
- Registering PersonalAI as a client on the provider is a one-time manual step on that other service —
  not automated from this repo.

## SSE Streaming Protocol

All streaming endpoints (chat, skill run, agent run, model pull) use **Server-Sent Events** over POST (since `EventSource` is GET-only).

Chat/skill/agent stream event types:

```jsonc
{ "type": "token", "text": "..." }               // Incremental text from the model
{ "type": "reasoning", "text": "..." }           // chat only — the model's thinking (DeepSeek reasoning_content / Ollama thinking); live-only, never persisted or replayed
{ "type": "tool-result", "toolInvocationId": "...", "toolName": "...", "resultContent": "...", "isError": false }
{ "type": "tool-progress", "toolInvocationId": "...", "toolName": "...", "message": "..." }  // mid-execution status, e.g. sandbox waiting for its host to boot
{ "type": "approval-required", "toolInvocationId": "...", "toolName": "...", "resolvedPath": "...", "reason": "..." }  // chat only
{ "type": "run-started", "agentRunId": "..." }     // agent runs only — lets a live viewer deep-link to history
{ "type": "llm-call", "totalCalls": 3 }           // chat + agent runs — just before each provider call (chat: session lifetime total; agent: the run's count)
{ "type": "token-usage", "totalInputTokens": 1200, "totalOutputTokens": 340 } // chat only — after each provider call that reported usage; session lifetime totals
{ "type": "turn-started", "afterSequenceNumber": 7 } // chat only, first event — messages above it belong to this turn (hidden while streaming to avoid duplicates)
{ "type": "done" }                                 // Turn/run finished
{ "type": "error", "message": "..." }              // Fatal error
```

---

See [Sandbox Architecture](SANDBOX_ARCHITECTURE.md) for the Docker sandbox broker design, and [Configuration](CONFIGURATION.md) for all config keys.
