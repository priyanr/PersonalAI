# Deployment

PersonalAI ships two supported ways to run: **Docker Compose** (everything in containers) and
**`dotnet run` + `npm run dev`** (API and UI as local processes, dependencies in Docker). The README has
the quick-start versions; this page has the details.

## Docker Compose

Full 5-service stack defined in `docker-compose.yml`:

| Service | Image | Port | Purpose |
|---------|-------|------|---------|
| `ollama` | `ollama/ollama:latest` | 11434 | Local LLM inference engine |
| `searxng` | `searxng/searxng:latest` | 8888 | Self-hosted meta-search for the `web_search` tool |
| `api` | Built from `src/PersonalAI.Api/Dockerfile` | `API_PORT` (5080) | .NET backend API |
| `web` | Built from `web/Dockerfile` | `WEB_PORT` (3000) | Next.js frontend |
| `sandboxhost` | Built from `src/PersonalAI.SandboxHost/Dockerfile` | *(none — dials out)* | Sandbox broker: mounts `/var/run/docker.sock` and `SANDBOX_HOST_PATH`, dials the `api` service's `/api/sandbox/host` WebSocket |

### Setup

```bash
cp .env.example .env
# Optional: set SANDBOX_HOST_KEY (enables the sandbox) and AUTH_* (enables login)
docker compose up -d --build
```

**Environment variables** in `.env` (all optional — see `.env.example` for the full list):

- `API_PORT`, `WEB_PORT` — host ports for the API and UI.
- `OLLAMA_DATA_PATH`, `SQLITE_DATA_PATH`, `WORKSPACE_DATA_PATH` — host folders for persistent data.
- `AUTH_ENABLED`, `AUTH_*` — OAuth2 / OIDC login (see the README's *Authentication* section and
  [Configuration](CONFIGURATION.md#authentication-configuration)).
- `SANDBOX_HOST_KEY` — shared secret between `api` and `sandboxhost`; empty disables the sandbox.
- `SANDBOX_HOST_PATH` — directory **on the Docker host** holding each sandbox's `/work` files.

**Key points:**
- The web image bakes `NEXT_PUBLIC_API_BASE_URL` in at build time (`http://localhost:${API_PORT}`), so
  rebuild it (`docker compose up -d --build web`) after changing `API_PORT` or when the browser reaches
  the API under a different hostname.
- `SANDBOX_HOST_KEY` is passed to **both** the `api` (`Sandbox__HostKey`) and `sandboxhost`
  (`Broker__HostKey`) services. With it empty, the broker connects, is rejected with 401 and keeps
  retrying — harmless, but you can also just not start it: `docker compose up -d ollama searxng api web`.
- `SANDBOX_HOST_PATH` is mounted at the **same path** inside the broker container, because the Docker
  daemon resolves sandbox bind mounts against the host's filesystem. It must be an absolute path the
  daemon can bind (on macOS, somewhere under a Docker Desktop shared folder such as `/Users/<you>/...`).
- Data (SQLite DB, workspace, Ollama models) lives in the host folders above and survives
  `docker compose down`.
- `computer_control` is Windows-only and does nothing useful inside the Linux API container — run the
  API with `dotnet run` on Windows if you want it.

## Local processes (`dotnet run`)

Prerequisites: .NET 10 SDK, Node.js 22+, and Docker (for Ollama/SearXNG and, optionally, the sandbox).

```bash
# 1. Dependencies (or point Ollama__BaseUrl / Search__SearxngUrl at your own instances)
docker compose up -d ollama searxng

# 2. API -> http://localhost:5080 (applies EF Core migrations on startup; DB file: personalai.db)
dotnet run --project src/PersonalAI.Api

# 3. UI -> http://localhost:3000
cd web && npm install && npm run dev
```

On Windows, `.\run.ps1` starts steps 2 and 3 in separate PowerShell windows.

Configuration comes from `src/PersonalAI.Api/appsettings.json`; override any key with an environment
variable using the `Section__Key` convention (e.g. `Ollama__BaseUrl`, `Sandbox__HostKey`, `Auth__Enabled`).
Relative paths in the defaults (`personalai.db`, `./data/workspace`, `./data/tool-output`, `log-*.txt`)
resolve against the API's content root / working directory; set absolute paths via
`ConnectionStrings__Default` and `Workspace__RootPath` if you want the data somewhere specific.

### Sandbox with `dotnet run`

The API side only needs a host key:

```powershell
# PowerShell
$env:Sandbox__HostKey = "<long random string>"
dotnet run --project src/PersonalAI.Api
```

```bash
# bash / zsh
Sandbox__HostKey="<long random string>" dotnet run --project src/PersonalAI.Api
```

Then start a broker, either way:

**A. Broker in Docker (any OS — recommended).** Start only the `sandboxhost` service and point it at the
API on the host:

```bash
# in .env
SANDBOX_HOST_KEY=<same string>
SANDBOX_BROKER_API_URL=ws://host.docker.internal:5080/api/sandbox/host

docker compose up -d --build --no-deps sandboxhost
```

**B. Broker as a local process (Linux / macOS).**

```bash
Broker__HostKey="<same string>" \
Broker__ApiUrl="ws://localhost:5080/api/sandbox/host" \
Broker__SandboxRootPath="$HOME/personalai-sandbox" \
dotnet run --project src/PersonalAI.SandboxHost
```

(On Windows, `Broker__DockerEndpoint=npipe://./pipe/docker_engine` talks to Docker Desktop, but option A
avoids Windows/Linux path translation entirely.)

The **Settings → Tools** page shows whether a sandbox host is connected.

## Publishing without Docker

```bash
dotnet publish src/PersonalAI.Api -c Release -o out/api
cd web && npm install && npm run build      # NEXT_PUBLIC_API_BASE_URL must be set at build time
```

The web build uses Next.js `standalone` output; copy `.next/standalone/`, `.next/static/` (to
`.next/static`) and `public/` together and run `node server.js` (this mirrors `web/Dockerfile`). Keep the
SQLite DB and workspace outside the publish folder by setting `ConnectionStrings__Default` and
`Workspace__RootPath`.

### Sandbox broker on a separate Docker host (systemd)

When the API runs somewhere other than the Docker host, run the broker on the Docker host under systemd.
`src/PersonalAI.SandboxHost/personalai-sandboxhost.service` is a ready-to-edit unit file with install
steps in its header. Set `Broker__ApiUrl` to the API's public `wss://.../api/sandbox/host` URL, and make
sure any reverse proxy in front of the API forwards WebSocket `Upgrade`/`Connection` headers for that path.

### Serving the UI under a sub-path

Set `NEXT_PUBLIC_BASE_PATH` (e.g. `/ai`) at **build** time of the web image/app. Remember to register the
matching `.../auth/callback` redirect URI with your OAuth provider if auth is enabled.

### Scaling constraints

Run **one** API replica: the agent scheduler is in-process and the API holds the single sandbox-broker
WebSocket in memory. The UI is stateless and can be scaled freely.

---

See [Configuration](CONFIGURATION.md) for all config keys and [Concepts](CONCEPTS.md) for architecture details.
