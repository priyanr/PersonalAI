# Configuration

## Environment Setup

All backend config is in `appsettings.json` (overridable via env vars using the `Section__Key` convention):

| Section | Key Settings | Type | Default |
|---------|-------------|------|---------|
| `ConnectionStrings` | `Default` | Connection string | `Data Source=personalai.db` (SQLite) |
| `Ollama` | `BaseUrl`, `DefaultSystemPrompt`, `RequestTimeoutSeconds` | URLs, text, seconds | `http://localhost:11434`, (empty), 300 |
| `Search` | `SearxngUrl`, `MaxResults`, `Enabled`, `Engines` | URL, int, bool, CSV | `http://localhost:8888`, 10, true |
| `Workspace` | `RootPath` | Directory path | `./workspace` |
| `Agent` | `MaxIterations`, `MaxOutputTokens`, `ToolResultMaxChars`, `HistoryKeepRecentMessages` | Integers | 25, 8192, 4000, 40 |
| `Scheduler` | `PollIntervalSeconds` | Seconds | 30 |
| `Auth` | `Enabled`, `Authority`, `ClientId`, `ClientSecret`, `RedirectUri`, `Scope`, ... (see below) | Various | See Auth section |
| `Gmail` | `FrontendBaseUrl` | URL | `http://localhost:3000` (for callback redirect) |
| `HttpTool` | `AllowedUrls`, `TimeoutSeconds`, `MaxResponseBodyChars` | Glob patterns, seconds, bytes | `[]`, 30, 65536 |
| `NetworkScan` | `AllowedRanges`, `MaxHosts`, `MaxConcurrency`, `TcpTimeout`, ... | CIDR, int, milliseconds | RFC1918 ranges, 256, 4, 3000 |
| `Sandbox` | `Enabled`, `HostKey`, `DefaultImage`, `AllowedImages`, timeouts, caps | Various | Detailed below |

## Authentication Configuration

Full OAuth2/OIDC setup (delegated login). When `Auth:Enabled = false` (default), the entire app is open.

```json
"Auth": {
  "Enabled": false,
  "Authority": "https://login.example.com/realms/main",
  "ClientId": "personalai",
  "ClientSecret": "...",
  "RedirectUri": "http://localhost:3000/auth/callback",
  "Scope": "openid profile email offline_access",
  "AuthorizeParameters": {},
  "UseDiscovery": true,
  "MetadataAddress": "",
  "RequireHttpsMetadata": true,
  "AuthorizationEndpoint": "",
  "TokenEndpoint": "",
  "RevocationEndpoint": "",
  "IntrospectionEndpoint": "",
  "EndSessionEndpoint": "",
  "JwksUri": "",
  "JwtKey": "",
  "Issuer": "",
  "Audience": "personalai",
  "ClientIdClaimTypes": ["client_id", "azp", "cid", "appid"],
  "ValidateClientId": true,
  "IntrospectAccessTokens": true,
  "ValidateCacheSeconds": 60,
  "PostLogoutRedirectUri": "http://localhost:3000"
}
```

**Key points:**
- Set `Authority` to your OIDC provider (Keycloak, Auth0, Okta, Entra ID, etc.).
- Enable `UseDiscovery` and clear explicit endpoints if the provider has a standard `.well-known/openid-configuration`.
- `RedirectUri` is the **frontend's** own `/auth/callback` route — the provider redirects the browser there directly, not through the API.
- `JwtKey` is used only for HMAC-signed tokens; omit it for RSA/ECDSA (use `JwksUri` instead).
- Registering PersonalAI as a client on the provider is a one-time manual step — not automated.
- A plain OAuth2 provider **without** discovery works too: set `UseDiscovery: false` and fill in the
  explicit endpoints (absolute URLs, or paths relative to `Authority`), plus `JwtKey` if it signs JWTs
  with a shared HMAC secret. See the README's *Authentication* section for worked examples.

## Sandbox Configuration

Enables the `sandbox` tool — Docker containers per chat session / skill / agent run.

```json
"Sandbox": {
  "Enabled": true,
  "HostKey": "...",
  "DefaultImage": "ubuntu:22.04",
  "AllowedImages": ["ubuntu:22.04", "python:3.11", "node:20"],
  "ExecTimeoutSeconds": 300,
  "OutputCapBytes": 1000000,
  "HeartbeatIntervalSeconds": 30,
  "IdleTimeoutMinutes": 10,
  "ReapOrphanedAfterMinutes": 60,
  "RequireApprovalForDangerousCommands": true,
  "DangerousCommandPatterns": ["^sudo", "^rm -rf", "^apt install", ...]
}
```

**Key points:**
- `HostKey` is a shared secret between the API and `PersonalAI.SandboxHost` broker. Leave empty to disable the sandbox entirely.
- `AllowedImages`: the model can only use images on this list; `DefaultImage` is used if the model doesn't specify one.
- `ExecTimeoutSeconds`: wall-clock timeout per `exec` call.
- `DangerousCommandPatterns`: regexes that trigger approval flow (see [Concepts: Tool System](CONCEPTS.md#tool-system)).
- Set `RequireApprovalForDangerousCommands = false` to auto-approve everything (not recommended in production).

## AI Provider Configuration

Backend config for Ollama + hosted provider defaults:

```json
"Ollama": {
  "BaseUrl": "http://localhost:11434",
  "DefaultSystemPrompt": "You are a helpful assistant.",
  "RequestTimeoutSeconds": 300
}
```

Each provider (Ollama, OpenAI, DeepSeek) has its own configuration stored in the **`AIProviderConfig`** table and edited via `/settings/providers`. The config cache is reloaded on every write to ensure consistency.

### Per-model settings

Models page → **Configure** pins request capabilities per model (stored in **`ModelConfig`**, mirrored in `AIProviderConfigCache`). Every field defaults to auto.

| Field | Values | Providers |
|-------|--------|-----------|
| `OutputLimitParameter` | `auto`, `max_tokens`, `max_completion_tokens`, `none` (send neither) | OpenAI, Azure, DeepSeek |
| `MaxOutputTokens` | 1 – 1,000,000 | all |
| `ToolCalling` | `auto`, `enabled`, `disabled` (no tools sent) | all |
| `ReasoningEffort` | `auto` (not sent), `none`, `minimal`, `low`, `medium`, `high`, `xhigh` | OpenAI, Azure, DeepSeek |
| `Api` | `auto`, `chat_completions`, `responses` | OpenAI, Azure |
| `Thinking` | `auto` (session toggle), `on`, `off` | Ollama, DeepSeek |

Max output tokens resolve model → provider `MaxOutputTokens` → `Agent:MaxOutputTokens`. On auto, the output-limit parameter is guessed from the model name; if the endpoint answers `400` rejecting it (e.g. *"Use 'max_completion_tokens' instead"*), the call is retried with the other parameter and the choice is remembered for that model until restart. A pinned value is never auto-swapped.

Some reasoning models refuse function tools on `/chat/completions` unless `reasoning_effort` is `none` (*"use /v1/responses or set reasoning_effort to 'none'"*). On auto the call is retried with `none`; if the model also rejects `none` (e.g. `gpt-6-astra`), or a different effort is pinned, the model is switched to the Responses API (`/responses`) for the rest of the process lifetime. On `/responses` the whole conversation is sent on every call (`store: false`), tools use the flat function format, a pinned effort goes in `reasoning.effort`, and reasoning summaries (`reasoning.summary: auto`) stream as reasoning deltas — dropped automatically if the model or organization refuses them. Pin `Api` to `responses` to skip the failed round trips, or to `chat_completions` to disable the switch.

**Per-chat / per-agent reasoning effort.** A chat session (chat settings) and an agent (agent form) can each choose their own `reasoningEffort` (`none` … `xhigh`, or blank for the model default). It takes precedence over the model's pinned `ReasoningEffort` and is sent on every call of that chat's turns or agent's runs (not on history compaction or skill runs). Ollama has no `reasoning_effort`; there the effort becomes `think`: `none` → `false`, `minimal`/`low` → `"low"`, `medium` → `"medium"`, `high`/`xhigh` → `"high"` (levels are honored by models such as gpt-oss; other thinking models treat them as on). Thinking switched off — by the chat or the model's `Thinking` pin — still wins.

## Database Schema

EF Core / SQLite entities:

| Entity | Purpose |
|--------|---------|
| `ChatSession` | A conversation thread: provider, model, system prompt, per-session settings, tool toggles |
| `ChatMessage` | Ordered messages (User, Assistant, Tool, System) with optional tool call JSON |
| `ToolInvocation` | Records every tool execution: args, status (Completed/PendingApproval/Approved/Denied/Failed), result |
| `AppSettings` | Singleton row for global config (web search guidance text, thinking enabled default) |
| `AIProviderConfig` | One configured field for one AI provider (e.g. `openai`/`ApiKey`); plaintext SQLite |
| `ModelConfig` | One per-model setting (e.g. `azure`/`gpt-6-astra`/`OutputLimitParameter`) |
| `Secret` | A user-defined key/value secret Skills can read; plaintext SQLite |
| `ToolConfig` | One configured field (visible or hidden) for one tool (e.g. `gmail`/`ClientId`); plaintext SQLite |
| `Skill` | Prompt + tool allowlist + secret allowlist |
| `Agent` | Prompt + skill allowlist + schedule (Manual/Once/Interval/Cron) |
| `AgentRun` | One recorded execution of an Agent: trigger, status, summary, JSON transcript |
| `SandboxInstance` | One Docker container: kind (Session/Ephemeral), owner id, image, container id, host path, status |

**Migrations** are in `PersonalAI.Core/Infrastructure/Migrations/` and auto-applied on startup via `db.Database.Migrate()`.

## Environment Variables (Docker Compose)

The `docker-compose.yml` maps `API_PORT`, `WEB_PORT`, `OLLAMA_PORT`, `SEARXNG_PORT`, and all `Auth__*`, `Sandbox__*`, etc. from a `.env` file. See `.env.example` for the full list.

**Key env vars:**
- `API_PORT` — API listen port (default 5080)
- `WEB_PORT` — UI listen port (default 3000)
- `AUTH_ENABLED` — Enable OAuth2 (default false)
- `AUTH_*` — OAuth2 config (plumbed as `Auth__*` to the API)
- `SANDBOX_HOST_KEY` — Shared secret for broker (plumbed to both API and `sandboxhost` service)
- `SANDBOX_*` — Sandbox config (plumbed as `Broker__*` to the broker)

---

See [Deployment](DEPLOYMENT.md) for Docker Compose stack setup and [Concepts](CONCEPTS.md) for details on each service's behavior.
