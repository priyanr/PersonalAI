# Frontend Architecture

Next.js 15 + React 19 + Redux Toolkit. **Read only if developing the UI.**

## App Router Pages

| Route | Purpose |
|-------|---------|
| `/` | Redirects to `/sessions` |
| `/sessions` | Chat list |
| `/sessions/[sessionId]` | Streaming chat interface with tool activity, approval prompts |
| `/models` | Model list table + pull form with progress bars (provider-aware) |
| `/agents` | Agent list |
| `/agents/new` | Create new agent |
| `/agents/[agentId]` | Edit agent, manual "Run now" button |
| `/agents/[agentId]/runs/[runId]` | Full transcript detail for one run |
| `/settings` | Tabs: General, Providers, Secrets, Skills, Tools |
| `/settings/providers` | Configure each `IAIProvider`'s fields |
| `/settings/secrets` | Add/edit/delete key-value secrets |
| `/settings/skills` | Skill list |
| `/settings/skills/new` | Create skill |
| `/settings/skills/[skillId]` | Edit skill + "Run" panel |
| `/settings/tools` | Per-tool settings (e.g. Gmail OAuth connect button) |
| `/providers`, `/secrets`, `/skills`, `/skills/new`, `/skills/[skillId]` | Thin redirects to `/settings/*` (kept for old links) |
| `/auth/callback` | OAuth2 redirect target; completes login then routes to `/sessions` |

## Layout Components

**`PrimaryTabs`** (top level)
- Chat / Agents tabs
- Logout button (when auth enabled)

**`SettingsTabs`** (within `/settings`)
- General / Providers / Secrets / Skills / Tools

## Redux Store

One slice per domain area, each with a `use*` hook in `src/hooks/`:

| Slice | Purpose |
|-------|---------|
| `sessionsSlice` | Chat session CRUD, current session state |
| `chatSlice` | Streaming state (tokens, tokens streamed, etc.) |
| `modelsSlice` | Available models per provider |
| `providersSlice` | AI provider configs |
| `secretsSlice` | Secrets vault |
| `skillsSlice` | Skills |
| `agentsSlice` | Agents + run history |
| `authSlice` | Access/refresh/ID tokens, subject |
| `toolsSlice` | Tool definitions + settings |

## Client Libraries

**`lib/api.js`** — HTTP client
```javascript
apiGet(path)
apiPost(path, body)
apiPatch(path, body)
apiPut(path, body)
apiDelete(path)
```
Every call awaits `getValidAccessToken()` and attaches it as `Authorization: Bearer ...`. A `401` redirects to login.

**`lib/sse.js`** — Custom SSE client
```javascript
streamSse(method, path, body, onEvent)
```
POSTs to SSE endpoints (native `EventSource` is GET-only) and parses `data:` frames from a `ReadableStream`.

**`lib/auth.js`** — OAuth2 client
```javascript
getValidAccessToken()
refreshAccessToken()
logout()
```
Manages tokens in `localStorage`, single-flight refresh with Web Lock across tabs, refresh-and-retry on 401.

## Component Tree

**Chat session:**
- `ChatInterface` — Main chat UI
  - `ChatMessages` — Message list (User, Assistant, Tool result)
  - `ChatInput` — User message textarea + send button
  - `ToolActivity` — Real-time tool invocation status (in-flight, approval pending, etc.)
  - `ApprovalPrompt` — Approve/deny dangerous file ops or sandbox exec

**Settings:**
- `ProviderSettings` — Configure each AI provider (schema-driven forms)
- `SecretsVault` — Add/edit/delete secrets
- `SkillsList` / `SkillEditor` — Skill CRUD + run panel
- `ToolSettings` — Generic tool settings + Gmail OAuth connect button
- `SandboxStatus` — Broker connection indicator

**Models:**
- `ModelList` — Available models table
- `ModelPull` — Pull form with progress bars (Ollama only)

**Agents:**
- `AgentsList` / `AgentEditor` — Agent CRUD + schedule config
- `RunHistory` — Agent run list
- `RunTranscript` — Full run transcript detail

---

See [Frontend source code](../web/src/) for component implementations. **Do not read this file unless you're developing the UI.**
