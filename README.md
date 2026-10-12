# PersonalAI

A self-hosted personal AI agent platform. Chat with local or hosted LLMs that can use tools: web search,
files, HTTP, Gmail, LAN scanning, desktop control, and a full **Docker sandbox** for running arbitrary
code. Write reusable prompt-driven **Skills**, and schedule **Agents** to run them on a cron. Everything
sits behind one ASP.NET Core API and one Next.js UI, and runs on your own machine.

- **Pluggable AI providers**: Ollama (local), OpenAI, Azure OpenAI, DeepSeek, all behind one `IAIProvider` abstraction
- **Agentic tool-use loop** with streaming (SSE), human approval for risky actions, stop/resume, and reattach after a page reload
- **11 built-in tools**: `web_search`, `read_file`, `write_file`, `edit_file`, `list_files`, `search_files`, `http_request`, `gmail`, `network_scan`, `computer_control`, `sandbox`
- **Docker sandbox**: one container per chat session or agent run, with a file browser, port forwarding, and downloads
- **Skills and scheduled Agents**: reusable prompts, and cron/interval agents that orchestrate them
- **Secrets vault**: store API keys once and reference them from skills with no plaintext in prompts
- **Long-conversation compaction**: old history is summarized instead of dropped
- **Optional login** through any OAuth2 / OpenID Connect provider (Keycloak, Auth0, Okta, Entra ID, Authentik, ...)

## Demo

[![PersonalAI demo video](https://img.youtube.com/vi/w34h43MJ8cs/maxresdefault.jpg)](https://www.youtube.com/watch?v=w34h43MJ8cs)

> [!WARNING]
> PersonalAI gives an LLM real capabilities: it can run code in containers, call HTTP endpoints, scan your
> LAN and (on Windows) drive your mouse and keyboard. **Auth is off by default**, so anyone who can reach
> the API port has all of that. Keep it on `localhost`, or [enable authentication](#authentication)
> before you expose it to a network. The sandbox broker mounts the Docker socket, which is root-equivalent
> on the Docker host. Treat `SANDBOX_HOST_KEY` like a root password.

---

## Contents

- [Demo](#demo)
- [Requirements](#requirements)
- [Quick start: Docker Compose](#quick-start-docker-compose)
- [Quick start: dotnet run](#quick-start-dotnet-run)
- [First steps in the UI](#first-steps-in-the-ui)
- [Sandbox](#sandbox)
- [Authentication](#authentication)
- [Configuration](#configuration)
- [Architecture](#architecture)
- [Project structure](#project-structure)
- [Running tests](#running-tests)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [License](#license)

## Requirements

| To run with | You need |
|---|---|
| Docker Compose | Docker Engine / Docker Desktop with Compose v2 |
| `dotnet run` | [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js 22+](https://nodejs.org/), and Docker for Ollama, SearXNG and the sandbox (or your own Ollama/SearXNG) |

Hardware depends on the models you run. Small Ollama models (1–4B) run on CPU. Hosted providers
(OpenAI, DeepSeek, Azure) need only an API key.

## Quick start: Docker Compose

```bash
git clone https://github.com/priyanr/PersonalAI.git
cd PersonalAI
cp .env.example .env          # all values are optional; defaults work out of the box
docker compose up -d --build
```

| Service | URL |
|---|---|
| Web UI | http://localhost:3000 |
| API | http://localhost:5080 |
| Ollama | http://localhost:11434 |
| SearXNG | http://localhost:8888 |

Data is stored under `./data/` (SQLite DB, agent workspace, Ollama models) and survives
`docker compose down`.

To enable the Docker sandbox, set `SANDBOX_HOST_KEY` in `.env` before starting (see [Sandbox](#sandbox)).

## Quick start: dotnet run

Run the API and UI as local processes and keep only the dependencies in Docker:

```bash
# 1. Dependencies: Ollama + SearXNG
docker compose up -d ollama searxng

# 2. API -> http://localhost:5080 (creates/migrates the SQLite DB on startup)
dotnet run --project src/PersonalAI.Api

# 3. Web UI -> http://localhost:3000 (in a second terminal)
cd web
npm install
npm run dev
```

On Windows, `.\run.ps1` opens the API and UI in two PowerShell windows.

Already running Ollama or SearXNG yourself? Skip step 1 and point the API at them:
`Ollama__BaseUrl=http://localhost:11434`, `Search__SearxngUrl=http://localhost:8888` (environment
variables override `appsettings.json` with the `Section__Key` convention).

> The `computer_control` tool (mouse, keyboard, screenshots) only works when the API runs natively on
> **Windows**. It is unavailable inside the Linux container.

## First steps in the UI

1. **Get a model.** Go to **Models** and pull an Ollama model (e.g. `qwen3:4b`, `llama3.2:3b`). Or open
   **Settings → Providers** and enter an API key for OpenAI, Azure OpenAI or DeepSeek.
2. **Start a chat.** Create a new session, pick the model, and choose which tools it may use in the chat
   options. Risky actions (file access outside the workspace, dangerous sandbox commands) pause for your
   approval.
3. **Store secrets.** Put API keys in **Settings → Secrets** and reference them from Skills.
4. **Create Skills and Agents.** A Skill is a reusable prompt plus a tool set. An Agent runs Skills on a
   schedule (cron or interval) or on demand, with a full run history.
5. **Optional tools.** `http_request` only reaches URLs you allow-list (`HttpTool:AllowedUrls`). Gmail
   needs your own Google OAuth client, entered under **Settings → Tools**; that page shows the redirect URI
   to register with Google.

## Sandbox

The `sandbox` tool gives the model a real Linux container (default `python:3.12-slim`; also
`node:22-slim` and `debian:bookworm-slim`). In it the model can run shell commands, read and write files,
install packages, and expose ports. You get one container per chat session or agent run, with a
persistent `/work` directory you can browse and download from the UI.

The API never talks to Docker directly. A small broker, **PersonalAI.SandboxHost**, runs next to Docker
and dials **out** to the API over a WebSocket, authenticated with a shared key. The Docker host therefore
needs no inbound ports and can be a different machine from the API.

```
 Browser ──HTTP/SSE──▶ PersonalAI.Api ◀──WebSocket (shared key)── PersonalAI.SandboxHost ──▶ Docker
                                                                     (dials out)            containers
```

### Enable it with Docker Compose

```bash
# .env
SANDBOX_HOST_KEY=<long random string>     # openssl rand -hex 32  |  PowerShell: [guid]::NewGuid().ToString("N")
SANDBOX_HOST_PATH=/data/sandbox           # folder on the Docker host for sandbox files
```

```bash
docker compose up -d --build
```

The same key goes to both the `api` and `sandboxhost` services. **Settings → Tools** shows whether the
sandbox host is connected. Then turn on the `sandbox` tool for a chat.

`SANDBOX_HOST_PATH` must be an absolute path the Docker daemon can bind-mount. The default works on Linux
and on Docker Desktop for Windows. On **macOS**, use a folder under a Docker Desktop shared directory
(e.g. `/Users/<you>/personalai-sandbox`).

### Enable it with dotnet run

Give the API the key:

```powershell
$env:Sandbox__HostKey = "<long random string>"      # PowerShell
dotnet run --project src/PersonalAI.Api
```

```bash
Sandbox__HostKey="<long random string>" dotnet run --project src/PersonalAI.Api   # bash/zsh
```

Then run the broker. The easiest way on any OS is in Docker, pointed at the API on your host:

```bash
# .env
SANDBOX_HOST_KEY=<same string>
SANDBOX_BROKER_API_URL=ws://host.docker.internal:5080/api/sandbox/host
```

```bash
docker compose up -d --build --no-deps sandboxhost
```

On Linux or macOS you can also run the broker natively:

```bash
Broker__HostKey="<same string>" \
Broker__ApiUrl="ws://localhost:5080/api/sandbox/host" \
Broker__SandboxRootPath="$HOME/personalai-sandbox" \
dotnet run --project src/PersonalAI.SandboxHost
```

### Remote Docker host

To run sandboxes on a separate machine, install the broker there as a systemd service. See
[`src/PersonalAI.SandboxHost/README.md`](src/PersonalAI.SandboxHost/README.md) and the unit file next
to it. Any reverse proxy in front of the API must forward WebSocket upgrade headers for
`/api/sandbox/host`.

### Hardening

Sandbox defaults are permissive: network on, root user, no CPU/memory limits. You can tighten them
without code changes through the `Sandbox` section (`NetworkEnabled`, `RunAsNonRoot`, `CapDrop`,
`AllowedImages`, timeouts and size caps). See
[docs/SANDBOX_ARCHITECTURE.md](docs/SANDBOX_ARCHITECTURE.md) for the full design and security model.

## Authentication

Authentication is **off by default** (`AUTH_ENABLED=false`) and the app is fully open. That is fine for a
single user on `localhost`. To require login, connect PersonalAI to **any OAuth2 / OpenID Connect
provider**. PersonalAI has no user database of its own. It uses the Authorization Code flow with PKCE, and
the browser sends the provider's access token as a `Bearer` header (no cookies).

### 1. Register PersonalAI with your provider

Create a **confidential web client** (it has a client secret) with:

| Setting | Value |
|---|---|
| Grant type | Authorization Code (PKCE supported) + Refresh Token |
| Redirect URI | `http://localhost:3000/auth/callback` (the **UI's** callback route, not the API's) |
| Post-logout redirect (optional) | `http://localhost:3000/` |
| Access token format | **JWT** (the API validates access tokens as JWTs) |

### 2. Configure PersonalAI

**Standard OIDC provider (recommended).** Endpoints and signing keys are discovered from
`{AUTH_AUTHORITY}/.well-known/openid-configuration`.

```bash
# .env (Docker Compose)
AUTH_ENABLED=true
AUTH_AUTHORITY=https://login.example.com/realms/main
AUTH_CLIENT_ID=personalai
AUTH_CLIENT_SECRET=<client secret>
AUTH_REDIRECT_URI=http://localhost:3000/auth/callback
AUTH_SCOPE=openid profile offline_access
# AUTH_AUDIENCE=<api identifier>    # if your provider puts one in `aud` and you want it checked
```

For `dotnet run`, set the same values as `Auth__*` environment variables or in
`src/PersonalAI.Api/appsettings.json`:

```json
"Auth": {
  "Enabled": true,
  "Authority": "https://login.example.com/realms/main",
  "ClientId": "personalai",
  "ClientSecret": "<client secret>",
  "RedirectUri": "http://localhost:3000/auth/callback",
  "Scope": "openid profile offline_access",
  "UseDiscovery": true
}
```

Provider notes:

| Provider | `Authority` | Notes |
|---|---|---|
| Keycloak | `https://<host>/realms/<realm>` | Client authentication **on**, standard flow enabled. |
| Auth0 | `https://<tenant>.auth0.com/` | Create an API and set `AUTH_AUDIENCE` to its identifier. Also pass it at login: `Auth__AuthorizeParameters__audience=<identifier>`. |
| Microsoft Entra ID | `https://login.microsoftonline.com/<tenant-id>/v2.0` | Expose an API scope (e.g. `api://<client-id>/access`) and add it to `AUTH_SCOPE` so the access token is a JWT for this app. |
| Okta | `https://<org>.okta.com/oauth2/default` | Use a custom authorization server so access tokens are JWTs. |
| Authentik / Zitadel / others | the issuer URL | Anything with a standard discovery document works. |

**Plain OAuth2 provider without discovery.** Turn discovery off and list the endpoints. They can be
absolute URLs or paths relative to the authority. If the provider signs JWTs with a shared HMAC secret
instead of publishing a JWKS, set `AUTH_JWT_KEY` too:

```bash
AUTH_ENABLED=true
AUTH_AUTHORITY=https://auth.example.com
AUTH_USE_DISCOVERY=false
AUTH_AUTHORIZATION_ENDPOINT=/oauth2/authorize
AUTH_TOKEN_ENDPOINT=/oauth2/token
AUTH_REVOCATION_ENDPOINT=/oauth2/revoke          # optional
AUTH_INTROSPECTION_ENDPOINT=/oauth2/introspect   # optional; enables server-side revocation checks
AUTH_JWT_KEY=<shared HMAC signing key>           # only for HMAC-signed tokens
AUTH_CLIENT_ID=...
AUTH_CLIENT_SECRET=...
AUTH_SCOPE=<scopes your provider expects>
```

Every option, including `AUTH_ISSUER`, `AUTH_TOKEN_ENDPOINT_AUTH_METHOD` (`client_secret_post` /
`client_secret_basic`), client-id claim checks and introspection caching, is documented in
[docs/CONFIGURATION.md](docs/CONFIGURATION.md#authentication-configuration) and `AuthSettings.cs`.

> When you serve the UI on another host or port, update the provider's redirect URI, `AUTH_REDIRECT_URI`,
> and (for Docker) rebuild the web image so it knows the API URL.

## Configuration

| Where | What |
|---|---|
| `.env` (from `.env.example`) | Docker Compose: ports, data paths, auth, sandbox key |
| `src/PersonalAI.Api/appsettings.json` | All API settings: Ollama, Search, Workspace, Agent limits, Scheduler, Auth, HttpTool, NetworkScan, Sandbox |
| Environment variables | Override any key with `Section__Key`, e.g. `Agent__MaxIterations=40`, `Sandbox__AllowedImages__3=golang:1.23` |
| UI → Settings | Provider API keys, per-model options, secrets, tool settings (stored in SQLite) |
| `web/.env.local` (from `.env.local.example`) | `NEXT_PUBLIC_API_BASE_URL` for `npm run dev`, if the API isn't on `localhost:5080` |

SearXNG's `config/searxng/settings.yml` contains a placeholder `secret_key`. Change it if you expose
SearXNG beyond localhost.

Full reference: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Architecture

```
┌──────────────────────────┐
│  Browser (Next.js 16)    │  Chat · Models · Skills · Agents · Settings
└────────────┬─────────────┘
             │ HTTP + SSE (Bearer token when auth is on)
┌────────────▼─────────────────────────────┐        ┌──────────────────────┐
│  PersonalAI.Api  (ASP.NET Core, .NET 10)  │◀──────▶│ OAuth2 / OIDC (opt.) │
│  ├─ ChatAgentService   (agentic loop)     │        └──────────────────────┘
│  ├─ SkillExecutionService / Agents        │──────▶ Ollama · OpenAI · Azure · DeepSeek
│  ├─ AgentScheduler (cron, in-process)     │──────▶ SearXNG (web search)
│  ├─ ToolRegistry (11 tools)               │
│  ├─ SandboxHostRegistry  ◀── WebSocket ───┼──────┐
│  └─ EF Core ─▶ SQLite                     │      │ dials out, shared key
└───────────────────────────────────────────┘      │
                                      ┌─────────────┴────────────┐
                                      │ PersonalAI.SandboxHost   │──▶ Docker Engine
                                      │ (broker on Docker host)  │    one container per
                                      └──────────────────────────┘    session / agent run
```

Key design decisions:

1. **One agentic loop, three front-ends.** Chat, Skills and Agents all reuse the same loop with different inputs and outputs.
2. **Turns run server-side.** A turn keeps running if the browser disconnects; the UI re-attaches to the SSE stream on reload.
3. **Outbound-only sandbox broker.** The Docker host accepts no inbound connections.
4. **Bearer tokens only.** Login is delegated to a standard OAuth2/OIDC provider, and there are no cookies.
5. **Single API replica.** The scheduler and the broker connection live in-process.

Deep dives: [Architecture](docs/ARCHITECTURE.md) · [Core concepts](docs/CONCEPTS.md) ·
[Sandbox](docs/SANDBOX_ARCHITECTURE.md) · [Compaction](docs/COMPACTION_ARCHITECTURE.md)

## Project structure

```
PersonalAI/
├── src/
│   ├── PersonalAI.Api/               # ASP.NET Core Web API: controllers, auth, SSE, hosted services
│   ├── PersonalAI.Core/              # Domain: agent loop, AI providers, tools, skills, agents, EF Core
│   ├── PersonalAI.Sandbox.Protocol/  # Shared WebSocket wire protocol (API <-> broker)
│   └── PersonalAI.SandboxHost/       # Sandbox broker (runs next to Docker)
├── tests/PersonalAI.Tests/           # xUnit test suite
├── web/                              # Next.js frontend (React 19, Redux Toolkit)
├── config/searxng/                   # SearXNG settings
├── docs/                             # Architecture, API, configuration, design docs
├── docker-compose.yml                # ollama, searxng, api, web, sandboxhost
├── .env.example                      # Compose configuration template
└── run.ps1                           # Windows: start API + UI for local dev
```

## Running tests

```bash
dotnet test PersonalAI.slnx
```

A few sandbox tests need symlink support and are skipped automatically where it isn't available.

## Documentation

| Doc | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System design, solution layout, service layers |
| [docs/CONCEPTS.md](docs/CONCEPTS.md) | Agentic loop, tools, skills, agents, secrets, auth, SSE protocol |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | Every config section, model settings, database schema |
| [docs/API.md](docs/API.md) | REST/SSE endpoint reference |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Docker Compose, `dotnet run`, publishing, remote sandbox host |
| [docs/SANDBOX_ARCHITECTURE.md](docs/SANDBOX_ARCHITECTURE.md) | Sandbox broker design, lifecycle, security |
| [docs/COMPACTION_ARCHITECTURE.md](docs/COMPACTION_ARCHITECTURE.md) | Long-conversation history compaction |
| [docs/FRONTEND.md](docs/FRONTEND.md) | Next.js pages, Redux state, SSE client |
| [docs/TESTING.md](docs/TESTING.md) | Test suites and conventions |
| [docs/SANDBOX_ENHANCEMENT.md](docs/SANDBOX_ENHANCEMENT.md) | Design record: sandbox alternatives considered |
| [docs/MCP_PLAN.md](docs/MCP_PLAN.md) | Proposal: exposing the sandbox as an MCP server |
| [docs/OLLAMA_LOCAL_VS_WINDOWS_IGPU.md](docs/OLLAMA_LOCAL_VS_WINDOWS_IGPU.md) | Benchmark: Dockerized vs native Ollama on Windows |

## Contributing

Issues and pull requests are welcome. Before you open a PR:

- Run `dotnet test PersonalAI.slnx` and `npm run build` in `web/`.
- Add an EF Core migration for any entity change: `dotnet ef migrations add <Name> --project src/PersonalAI.Core --startup-project src/PersonalAI.Api`.
- Never commit real secrets. `.env`, `*.db` and `web/.env.local` are gitignored.

`CLAUDE.md` gives AI coding assistants a map of the codebase.

## License

[MIT](LICENSE)
