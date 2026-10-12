# PersonalAI.SandboxHost

The broker that runs *on the Docker host* and dials **out** to the PersonalAI API over one WebSocket
(`GET /api/sandbox/host`), authenticated by a shared key (`Broker:HostKey` here, `Sandbox:HostKey` on the
API — they must match exactly). The API never opens an inbound connection to this machine; this process
always initiates the connection, so the Docker host needs no inbound ports and no SSH keys.

It owns two things:

- **Docker Engine API access** (`DockerSandboxManager`) — create/exec/destroy/list containers, using
  `Docker.DotNet.Enhanced` against the local Docker socket.
- **Direct filesystem access** (`SandboxFileService`) to `{Broker:SandboxRootPath}/{ownerId}` — every file
  operation (read/write/edit/list/search/delete/move/mkdir) is plain `System.IO`, never `docker cp` or an
  `exec`, because this broker runs on the same machine as the files.

`SandboxPathResolver` is the security boundary that confines every model-supplied path to the owning
sandbox's directory — see its XML docs for the exact rules.

## Deployment mode 1: docker-compose

Add the `sandboxhost` service from the repo's `docker-compose.yml` — it mounts `/var/run/docker.sock` and
the sandbox root, and reads its `Broker__*` settings from environment variables (`.env`). This is the
simplest option when the Docker host running the sandboxes is the same machine as the rest of the compose
stack.

## Deployment mode 2: bare metal (systemd)

When the Docker host is a *different* machine from wherever the API runs (e.g. API on a server or in a
cluster, sandbox containers on a dedicated box), publish this project there directly and run it under systemd —
`personalai-sandboxhost.service` in this directory is a ready-to-edit unit file with install instructions
in its header comment. This avoids running the broker itself inside a container, which sidesteps the
bind-mount gotcha below entirely (the broker's own filesystem view and the Docker daemon's are simply the
same filesystem).

`Broker__HostKey` is never in the unit file or the repo: put it in the root-only
`/etc/personalai-sandboxhost/broker.env`, which the unit loads via `EnvironmentFile=`. Point
`Broker__ApiUrl` at the API's public `wss://` URL; any reverse proxy in front of the API must forward
the WebSocket `Upgrade`/`Connection` headers for `/api/sandbox/host`.

## The bind-mount path gotcha

`HostConfig.Binds` (used by `DockerSandboxManager.CreateAsync` to mount `{root}/{ownerId}` at `/work` in
every sandbox container) is resolved by the **Docker daemon**, not by this broker process. The daemon
always interprets that path relative to *its own* host filesystem.

If this broker itself runs inside a container (deployment mode 1), that means `Broker:SandboxRootPath`
— the path *this process* sees when it does `Directory.CreateDirectory` and files I/O — must be bind-mounted
at the **identical path** on the actual Docker host, or the two views diverge silently:

- Host has `/data/sandbox` (real directory).
- Broker container is started with `-v /data/sandbox:/data/sandbox` and `Broker__SandboxRootPath=/data/sandbox`.
- The broker's own `Directory.CreateDirectory("/data/sandbox/<owner>")` and the `HostConfig.Binds` string
  `"/data/sandbox/<owner>:/work"` it hands the daemon now refer to the **same** real directory, because
  both point at the same path string and that path happens to be identical on both sides of the broker's
  own mount.

Get this wrong (e.g. mount the host's `/data/sandbox` at some *other* container path like `/sandbox-data`,
while `Broker:SandboxRootPath` still says `/data/sandbox`) and every sandbox container the broker creates
will silently bind-mount an **empty** directory at `/work` — the broker's file I/O will work fine against
its own (correct) view, but the running container's `/work` won't be the same place, so `exec`ed commands
won't see the files the file tools wrote. There is no error; it just quietly doesn't work. `docker-compose.yml`
mounts both sides at `/data/sandbox` for exactly this reason — do not change one side without the other.

## Root-owned files

With the default permissive settings (`RunAsNonRoot: false` on the `CreateSandboxRequest`), sandbox
containers run as root, so any files they create on the bind-mounted host directory are root-owned. This
broker must run with enough privilege to read and, on destroy with `DeleteFiles: true`, delete them —
either as root (the systemd unit's default) or as a user in the `docker` group whose UID the sandboxes are
also configured to run as, if you enable `RunAsNonRoot` later.

## Configuration (`Broker` section / `Broker__*` env vars)

| Key | Default | Meaning |
|---|---|---|
| `ApiUrl` | `ws://localhost:5080/api/sandbox/host` | The API's sandbox WebSocket endpoint. |
| `HostKey` | *(empty)* | Shared secret sent as the `X-Sandbox-Host-Key` header. Must match the API's `Sandbox:HostKey`. |
| `HostId` | machine name | Identifies this broker to the API (`/api/sandbox/status`). |
| `SandboxRootPath` | `/data/sandbox` | Root directory for `{root}/{ownerId}` — see the bind-mount gotcha above. |
| `DockerEndpoint` | `unix:///var/run/docker.sock` | Passed to `DockerClientBuilder.WithEndpoint`. Use `npipe://./pipe/docker_engine` for local Windows dev. |
| `RequestTimeoutSeconds` | 30 | Timeout for a single Docker API call and the HostHello/HostHelloAck handshake. |
| `ImagePullTimeoutSeconds` | 300 | First use of an image may need to pull it. |
| `MaxListEntries` / `MaxSearchHits` | 500 / 200 | Caps mirrored from `PersonalAI.Core`'s `SandboxSettings` — kept as this project's own copy since it cannot reference Core. |
| `MaxFileTransferBytes` | 5 MiB | Cap on a single Read/Write payload. |
| `MaxExecOutputBytesPerStream` | 5 MiB | Raw-bytes safety net per exec stream, independent of the API's own (much smaller) per-stream char cap. |
| `ExecChunkBytes` | 32 KiB | Size of each streamed `ExecOutputChunk` before base64 inflation. |

## Reconnection

`BrokerConnection` reconnects with jittered exponential backoff, 1s up to a 30s cap, and backs off harder
(4x instead of 2x per attempt) after an explicit `HostHelloAck(Accepted: false)` — that's a configuration
error (wrong key, `Sandbox:Enabled` off), not a transient network blip, so hammering the API with retries
would not help.
