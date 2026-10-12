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
| `GET` | `/api/agents` | List agents |
| `POST` | `/api/agents` | Create an agent |
| `GET` | `/api/agents/{id}` | Get an agent |
| `PATCH` | `/api/agents/{id}` | Update an agent |
| `DELETE` | `/api/agents/{id}` | Delete an agent |
| `POST` | `/api/agents/{id}/run` | Trigger a manual run → **SSE stream** (includes `run-started`, `llm-call`, `token-usage`) |
| `GET` | `/api/agents/{id}/runs` | List an agent's run history (each with `llmCallCount`, `totalInputTokens`, `totalOutputTokens`) |
| `GET` | `/api/agents/{id}/runs/{runId}` | Full transcript for one run, plus the same counters |

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
