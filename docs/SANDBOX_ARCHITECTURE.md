# Sandbox Architecture

Every other tool runs inside the API process — file tools confined to `Workspace:RootPath`, `http_request`
URL-allow-listed, `computer_control` driving the host desktop — and none of them can run arbitrary code.
The `sandbox` tool closes that gap: a Docker container the model can run shell commands and file
operations in, one per chat session or one per skill/agent run. See
[`SANDBOX_ENHANCEMENT.md`](SANDBOX_ENHANCEMENT.md) for execution-model alternatives considered afterward
and why none of them replaced this.

## Why this shape

The API runs as a single process (locally, in Docker Compose, or in a cluster); the Docker host that runs sandbox
containers may be the same machine or a separate one. Rather than give the API inbound SSH to that host, a small broker
(`PersonalAI.SandboxHost`) runs *on* the Docker host and dials **out** to the API over one WebSocket,
authenticated by a shared key. The API multiplexes every sandbox operation — create, exec, file ops,
destroy — over that single connection.

- No inbound ports and no SSH keys on the Docker host.
- One artifact, one key, instead of SSH-for-lifecycle plus a per-container agent (two transports, two auth
  schemes).
- Sandbox images stay vanilla (`python:3.12-slim`, `node:22-slim`, ...). Nothing is baked into them,
  because the broker drives the Docker API from outside — no image needs to be rebuilt to add a runtime.

## Components

```
  API (single replica, any host)               Docker host
 +----------------------------------+          +--------------------------------------+
 | SandboxTool (ITool, singleton)   |          | PersonalAI.SandboxHost               |
 |   depends only on                |          |  (systemd / compose service)         |
 | ISandboxHostClient               |          |   - DockerSandboxManager -> Docker    |
 |   implemented by                 |  ONE WS  |     Engine API (create/exec/destroy/  |
 | SandboxHostRegistry <------------+----------+     list only)                        |
 | (singleton, owns the socket)     |  +hostKey |   - SandboxFileService -> plain file  |
 |                                  |          |     I/O, no Docker involved            |
 | GET /api/sandbox/host   (WS)     |          |                                        |
 | SandboxInstance table (EF)       |          |  [chat-sbx-A]      [agent-run-B]       |
 | SandboxReaperHostedService       |          |   /work             /work              |
 +----------------------------------+          |   bind-mounted from host:              |
                                                |   /data/sandbox/<ownerId>              |
                                                +--------------------------------------+
```

### `PersonalAI.Sandbox.Protocol`

A zero-package-reference shared project referenced by both `PersonalAI.Core` (API side) and
`PersonalAI.SandboxHost` (broker). Deliberately has no other dependency, so the broker never pulls in EF
Core, FlaUI, or `System.Drawing.Common` transitively through Core.

- `SandboxFrame` — a `[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]` hierarchy: `HostHello`,
  `HostHelloAck`, `Heartbeat`, `ListSandboxesRequest`/`Response`, `CreateSandboxRequest`/`Response`,
  `ExecRequest`, `ExecOutputChunk`, `ExecCompleted`, `FileOpRequest`/`Response`,
  `DestroySandboxRequest`/`Response`, `ErrorFrame`. Every frame carries a `RequestId` used for
  correlation.
- `SandboxKind` (Session / Ephemeral), `StreamKind` (Stdout / Stderr), `FileOp` (Read, Write, Edit, List,
  Search, Delete, Move, MakeDirectory, ReadRaw, Zip — the last two power the sandbox file browser's
  byte-exact download and archive-a-selection actions; see
  [Sandbox file browser](#sandbox-file-browser-human-facing-rest-surface)). Appended only, never inserted —
  the enum serializes as a plain int ordinal (no `JsonStringEnumConverter`), so inserting a value would
  silently desync a deployed broker/API pair.
- `IFrameTransport` + `WebSocketFrameTransport` — a thin wrapper over `System.Net.WebSockets.WebSocket`
  (BCL, no package needed) that reassembles fragments until `EndOfMessage`.
- `SandboxFrameLimits.MaxFrameBytesFor` — the one formula both `SandboxHostRegistry` (API) and
  `BrokerConnection` (broker) call to size `WebSocketFrameTransport`'s inbound cap off `MaxZipBytes`/
  `MaxDownloadBytes`; lives here so it's defined exactly once for two otherwise-independent processes.
- `SandboxFramePump` — the correlation engine shared by both ends. A single writer task drains an outbound
  `Channel<SandboxFrame>` (WebSocket sends are not thread-safe, so nothing else may call
  `transport.SendAsync` directly); a single reader task dispatches each inbound frame to a pending
  request's `TaskCompletionSource`, to a registered exec-chunk handler, or — if unmatched by either — to an
  `OnRequest` callback (how the broker, which only ever responds, observes inbound requests at all). A
  dispatch guard specifically prevents an `ExecOutputChunk` from ever completing the pending request in
  place of the terminal `ExecCompleted`. `FaultAllPending` fires every outstanding request with a
  `SandboxHostDisconnectedException` when the transport dies, so a caller fails fast instead of hanging.

### API side (`PersonalAI.Core` / `PersonalAI.Api`)

- **`ISandboxHostClient`** (`Core/Interfaces`) — the seam `SandboxTool` depends on: `IsConnected`,
  `EnsureSandboxAsync(sandboxId, kind, ownerId, image, ct)` returning the host path, `ExecAsync(...)`
  returning a `SandboxExecResult`, `FileOpAsync(FileOpRequest, ct)`, `DestroySandboxAsync(sandboxId,
  deleteFiles, ct)`.
- **`SandboxHostRegistry`** (singleton, implements `ISandboxHostClient`) — owns the one live connection.
  - Handshake (`HostHello`/`HostHelloAck`) happens directly over the raw `IFrameTransport`, before a
    `SandboxFramePump` is constructed — the pump's request/response model can't represent an unprompted
    opening frame.
  - Duplicate connection: newest wins. A restarted broker can leave a half-dead socket the API still
    believes is live; the new connection is swapped in and the old socket closed with
    `WebSocketCloseStatus.PolicyViolation`.
  - Heartbeat is implemented as a periodic `pump.SendRequestAsync(new Heartbeat(...))` call. **The broker
    must echo the frame back with the identical `RequestId`** — a `TimeoutException` there is treated as a
    dead connection and the socket is aborted. This is a protocol contract, not just an implementation
    detail: get it wrong on either end and the connection tears down every `HeartbeatTimeoutSeconds`.
  - `ReconcileAsync` runs after every successful handshake, in a fresh DI scope (the registry is a
    singleton; `ISandboxRepository` is scoped) — the same captive-dependency pattern `GmailTool` uses for
    its own scoped writes.
- **`SandboxReconciler`** (pure, static) — three outcomes from comparing the DB's `SandboxInstance` rows
  against the broker's reported container list:
  - DB row + container both alive -> mark `Running`.
  - DB row, no container -> mark `Orphaned`. The next tool call transparently creates a fresh container
    re-binding the same host directory — nothing is lost, because the directory (not the container) is the
    real persistence boundary.
  - Container the DB doesn't know about (DB reset, stale deploy) -> left untracked for the reaper's grace
    period rather than destroyed immediately, in case something is mid-debug on that host.
  - `ReconcileAsync` is not the only path to `Orphaned`, though — see "Mid-call self-heal on a dead
    container" below for the other one, which catches a container that dies without the broker connection
    itself ever dropping.
- **`BoundedOutputAccumulator`** — accumulates one exec stream (stdout or stderr) under a character
  budget, keeping both a head and a rolling tail rather than truncating from the front, because the
  diagnostic content of shell output (a stack trace, a build error) is usually at the end. Uses a stateful
  `Decoder` across chunks so a multi-byte UTF-8 character split at an arbitrary byte boundary isn't turned
  into a replacement character.
- **`SandboxRepository` / `ISandboxRepository`** (scoped, EF) — CRUD plus idle-candidate queries against
  `SandboxInstance`, with separate cutoffs for `Session` vs `Ephemeral` kinds. `GetMostRecentByOwnerAsync`
  is the one status-unfiltered lookup (it sees `Destroyed` rows too) — used only by `SandboxTool` to carry
  `PortsJson` forward and detect a prior sandbox across the idle reaper's `Destroyed` boundary; never used
  to feed `GetAllActiveAsync`/the host-sleep stop-signal decision, which must keep treating `Destroyed` as
  gone.
- **`SandboxTool`** (`Core/Tools`) — the model-facing tool, name `sandbox`, one `action` enum with ten
  actions: `exec`, `read_file`, `write_file`, `edit_file`, `list_files`, `search_files`, `delete`, `move`,
  `mkdir`, `ports`. The first nine deliberately mirror the names and semantics of the existing in-process
  file tools so there is nothing new for the model to learn; `ports` is sandbox-specific (see below).
  - Resolves the caller's `SandboxOwner` from `ToolExecutionContext`; a `ChatSession` owner maps to
    `SandboxKind.Session`, a Skill or Agent run owner maps to `SandboxKind.Ephemeral`.
  - Gets or creates the owner's `SandboxInstance` row through `IServiceScopeFactory` (the tool is a
    singleton, the repository is scoped — `GmailTool`'s captive-dependency workaround).
  - The container image is **tool-level configuration** (a `DefaultImage` setting, falling back to
    `Sandbox:DefaultImage`), never a per-call model argument, so the model cannot pick an arbitrary image.
    It's validated against the **union** of two allow-lists: `Sandbox:AllowedImages` (operator config —
    appsettings.json locally, `Sandbox__AllowedImages__0`/`__1`/... env vars in Compose — requires a
    process restart to change) and a second tool setting, `AdditionalAllowedImages` (a comma-separated
    list stored in `ToolConfig`, edited via `/settings/tools` like `DefaultImage`, picked up immediately
    with no restart since `ToolConfigCache` updates synchronously on the PATCH). Entries are trimmed and
    deduplicated; if both lists are empty, any image is allowed — same as the original single-list
    behaviour. This exists because the operator list is the right place for a deployment-wide policy but
    the wrong place for "I want to try a different image right now" — that's a live, no-restart knob.
  - Exec results render the exit code (and any timeout notice) on the **first** line, because history
    trimming truncates a tool result from the end — the single most important fact about a run must never
    land where truncation could eat it. A non-zero exit code is not a tool error (`IsError: false`); the
    model is expected to read it and react, the same posture `HttpTool` takes toward non-2xx responses.
  - File actions return compact confirmations ("Wrote 412 bytes to script.py", "Replaced 2 occurrences"),
    not echoed content.
  - `IsConnected(cache) => hostClient.IsConnected`; a disconnected broker short-circuits with a clear
    error rather than issuing a request that would just time out.
  - **`ports`** — returns the ports published for this owner's sandbox (`SandboxInstance.PortsJson`,
    populated once at creation, re-read on every call rather than re-asking the broker). No arguments; the
    tool's description tells the model to call this before starting a server it needs reachable from
    outside the container, since binding to any other port runs fine but isn't published. See "Exposed
    ports" below for how the ports themselves are chosen and published.
- **`ToolExecutionResult.cs`** — `SandboxOwnerKind` (ChatSession / SkillRun / AgentRun), the `SandboxOwner`
  record struct, and an optional `Sandbox` field added to `ToolExecutionContext`. The field is optional
  specifically so every pre-existing `new ToolExecutionContext(false)` construction across the codebase and
  test suite kept compiling unchanged.
- **`ChatAgentService` / `SkillExecutionService` / `AgentExecutionService`** — each threads a `SandboxOwner`
  into the `ToolExecutionContext` it builds. `ChatAgentService` uses the real session id; the two
  unattended services generate an in-memory run id (neither persists a session concept). Both unattended
  services destroy their ephemeral sandbox in a `finally` around the whole run (`deleteFiles: true`),
  skipping the call outright when no broker is connected — otherwise every run in a deployment that never
  enabled the feature would throw and log a warning. Destroy is idempotent, so a run that never touched the
  sandbox tool is a no-op.
- **`SandboxHostController`** (`Api/Controllers`) —
  - `GET /api/sandbox/host` — `[AllowAnonymous]` (the broker authenticates with the shared host key, not a
    user bearer token). Validates `Sandbox:Enabled`, a non-empty `HostKey`, and the `X-Sandbox-Host-Key`
    request header via `CryptographicOperations.FixedTimeEquals` over a hash of both sides (hashed first so
    a length mismatch can't leak key length through the comparison's own short-circuit) — all **before**
    `AcceptWebSocketAsync`. Any failure returns a plain `401` without ever upgrading.
  - `GET /api/sandbox/status` — normal auth, returns `{ connected, hostId, connectedAt, activeSandboxes }`
    for the settings UI.
- **`SandboxReaperHostedService`** (`Api/BackgroundServices`) — same `PeriodicTimer` / per-tick-scope /
  per-item-scope-with-try-catch shape as `AgentSchedulerHostedService`. Selection logic lives in a pure,
  unit-tested `SandboxReapPlanner` in the same file:
  - Idle chat-session sandboxes past `Sandbox:IdleTimeoutMinutes` are retired but their **files kept**
    (`deleteFiles: false`) — returning to the session later just spins a fresh container back onto the
    same host directory.
  - Idle ephemeral sandboxes past `Sandbox:EphemeralGraceMinutes` are destroyed **with** their files — the
    hard-kill safety net for a process that died before its own `finally` could run.
  - A session-kind sandbox whose owning `ChatSession` no longer exists has its files deleted regardless of
    idle state, checked via a lightweight `IChatSessionRepository.SessionExistsAsync` (a plain `AnyAsync`)
    rather than the full eagerly-loaded `GetSessionAsync`, since the reaper asks this about every live
    sandbox on every poll.

### Broker side (`PersonalAI.SandboxHost`)

A separate .NET Generic Host worker project, deployed to the Docker host (as a compose service or a systemd unit).

- **`BrokerConnection`** (`BackgroundService`) — dials the API with `ClientWebSocket`, presenting the
  shared key via the `X-Sandbox-Host-Key` request header (never a query string, to keep it out of access
  logs). Performs the `HostHello`/`HostHelloAck` handshake over the raw transport before constructing a
  `SandboxFramePump`. Reconnects with jittered exponential backoff (1s to a 30s cap), backing off harder
  after an explicit `HostHelloAck(Accepted: false)`, since that's a configuration error, not a transient
  one. Echoes every inbound `Heartbeat` frame back with the identical `RequestId` — the other half of the
  liveness contract described above.
- **`DockerSandboxManager`** — uses **`Docker.DotNet.Enhanced` 4.3.3**, not the unmaintained `Docker.DotNet`
  3.x line (stuck at 3.125.15 since ~2022, and — verified directly by inspecting the NuGet package — ships
  only `netstandard2.0`/`netstandard2.1` targets, no `net10.0`-specific one; `Docker.DotNet.Enhanced` ships
  a real `net10.0` target). Verified API surface differs from most online examples:
  `new DockerClientBuilder().WithEndpoint(uri).WithTimeout(ts).Build()` (not
  `DockerClientConfiguration(...).CreateClient()`); `client.Exec.CreateContainerExecAsync(...)` (not
  `ExecCreateContainerAsync`); `client.Exec.StartContainerExecAsync(id, new
  ContainerExecStartParameters{...}, ct)` returns the `MultiplexedStream` directly —
  `StartAndAttachContainerExecAsync` does not exist in this package; `ContainerExecCreateParameters.TTY`
  (capitalized), not `.Tty`.
  - **Create** — idempotent (adopts an existing same-named container rather than failing on Docker's 409);
    pulls the image if absent; sets `NanoCPUs`/`Memory`/`PidsLimit`/`CapDrop`/`User` only when the request
    actually carries them, since the defaults are unlimited and permissive by design.
  - **Exec** — streams output as ~32KB `ExecOutputChunk` frames with a monotonic per-stream `Sequence`,
    read via `MultiplexedStream.ReadOutputAsync`/`ReadResult`/`TargetStream` to demultiplex stdout from
    stderr; the terminal `ExecCompleted` carries the exit code from `InspectContainerExecAsync`, or
    `TimedOut: true`.
  - **Destroy** — stop + force-remove the container, deleting the owner directory only when told to.
  - **List** — label-filtered `ListContainersAsync(All: true)`; each reported `AdoptedSandbox.SandboxId`
    comes from the `personalai.sandbox.id` label, never the raw Docker container id, since that's what the
    API's reconciliation keys on.
- **`SandboxFileService`** — every `FileOp` as plain `System.IO` against
  `{Broker:SandboxRootPath}/{ownerId}`. No `docker cp`, no tar, no exec involved. Skips the same ignored
  directories (`.git`, `bin`, `obj`, `node_modules`, `.vs`, `.idea`, `dist`, `__pycache__`) the in-process
  `Workspace` class already ignores. Enforces `MaxListEntries`/`MaxSearchHits`/`MaxFileTransferBytes`.
- **`SandboxPathResolver` — the security boundary.** Every model-supplied path is resolved through it
  before any I/O happens, in two steps:
  1. A lexical check (`Path.GetFullPath` + `Path.GetRelativePath`) that rejects `..`, absolute/rooted
     paths outright (there is no human in the loop to approve an escape here, unlike the in-process
     `Workspace` tool's approval flow), and anything that normalizes outside the owner's root.
  2. **Symlink resolution along the full path** — walking each existing path segment and following it if
     it's a link, then re-running the same containment check against the resolved target.

  Step 2 exists because `Path.GetFullPath` does **not** follow symlinks. A model running `exec` inside its
  own container (root, no dropped capabilities by default) can do `ln -s / /work/escape`, and a purely
  lexical check reports the resulting path as "inside the root" — while the broker, which runs on the
  Docker **host**, would actually read or write through it onto the host filesystem. This was found during
  review and verified in a real Linux container both ways: before the fix, both a read and a write through
  such a symlink escaped successfully; after the fix, both are rejected, while a symlink that stays inside
  the sandbox continues to work correctly.

## Wire protocol summary

One WebSocket, JSON text frames, one frame per WebSocket message (no extra length-prefixing — the
WebSocket message boundary already provides framing). Every frame carries a `RequestId`; the responder
echoes it back. Exec is the one multi-frame exchange: N `ExecOutputChunk` frames followed by exactly one
terminal `ExecCompleted`, all sharing the originating `ExecRequest`'s `RequestId`.

Sequence on connect: `HostHello` -> `HostHelloAck` (raw transport, no pump yet) -> pump constructed on both
sides -> API sends `ListSandboxesRequest` -> broker replies with every `personalai.sandbox=true` labelled
container -> `SandboxReconciler` applies the three-way transition described above.

## Storage model

Each sandbox gets a host directory, `{Broker:SandboxRootPath}/{ownerId}` (default root `/data/sandbox`),
bind-mounted into the container at `/work`. No Docker named volume, and no bind-mount of the API's own
`Workspace:RootPath`.

- A plain host directory survives container removal and `docker volume prune`, is inspectable and
  backup-able directly on the host, and — since the broker runs on that same host — lets
  `SandboxFileService` do plain filesystem I/O instead of streaming tar archives through `docker cp`.
- The **directory**, not the container, is the persistence boundary: if a container is lost (crashed,
  `docker rm`'d, or marked `Orphaned` on reconnect), the next tool call transparently creates a fresh
  container re-binding the same directory, and the session's files are untouched.
- The container's own filesystem outside `/work` is **not** persisted — a fresh container always starts
  from the image's own filesystem there.

## Execution model

- Each `exec` is a fresh `docker exec` — there is no persistent shell process, so `cd` and other shell
  state do **not** carry over between calls. Installed packages and files under `/work` **do** persist,
  since those live in the container's writable layer and the bind mount respectively. The tool's schema
  documents this explicitly and recommends `/bin/sh -c "... && ..."` or an explicit `working_dir` argument
  instead of relying on shell state.
- `command` is passed as **argv** (a string array), never a shell string, to avoid quoting/injection
  issues; `/bin/sh -c` is the documented escape hatch when the model genuinely needs pipes, globs, or `&&`.
- Output is capped per stream at `Sandbox:MaxOutputCharsPerStream`, applied head+tail
  (`BoundedOutputAccumulator`) rather than head-only, since the diagnostic content of shell output is
  usually at the end.

## Exposed ports

Every sandbox container publishes a small, fixed number of ports (`Broker:ExposedPortCount`, default 5)
so a server the model starts inside it — a dev server, a quick HTTP endpoint to demonstrate something — is
reachable from outside without any port-forwarding request roundtrip. Host port always equals container
port, TCP only, so there is exactly one number for the model (and a human) to reason about.

- **Chosen from 20000–29999 by default** (`Broker:HostPortRangeStart`/`HostPortRangeEnd`) —
  deliberately *below* every OS ephemeral range (Linux 32768–60999, Windows 49152–65535). The original
  default was the IANA dynamic range 49152–65535, but the OS hands those same ports to outbound
  connections, so a sandbox could be given a port some socket grabbed a moment later and Docker's bind
  failed with "address already in use".
- **`SandboxPortAllocator`** (`PersonalAI.SandboxHost`, pure and unit-tested with an injected availability
  predicate — no socket or Docker daemon needed to test its selection logic) tracks which ports are
  currently handed out across every sandbox this broker process is managing, and picks free ones from the
  configured range for a new sandbox. `DockerSandboxManager` is the only real caller. Its availability
  check skips host ports Docker already publishes for *any* container and every local TCP port the host
  is using (listeners and the local end of connections), then runs a live bind-test with SO_REUSEADDR off,
  dual-stack. That is still a point-in-time probe, not a reservation, so a create that fails on a port
  conflict is retried (up to 3 attempts) without the port Docker blamed, keeping the other ports. A
  container that fails to start is removed, not left for the next ensure call to adopt, and an existing
  container that can't start because of a port conflict is recreated on new ports (files are in the
  bind mount, so nothing is lost).
- **Reused across recreation, not re-rolled.** The ports chosen for a sandbox are persisted on
  `SandboxInstance.PortsJson` and passed back as `CreateSandboxRequest.PreferredPorts` on any later
  `EnsureSandboxAsync` call for that same owner — after the container was orphaned and needs recreating,
  **or** after the idle reaper retired a chat-session sandbox (`Destroyed` status, files kept — see
  "Scoping and lifecycle") and a later tool call needs to recreate it. In the latter case
  `GetActiveByOwnerAsync` finds nothing (it filters `Destroyed` rows out), so `SandboxTool` falls back to
  `ISandboxRepository.GetMostRecentByOwnerAsync` — the same query without that status filter — to find the
  retired row's `PortsJson` and seed it onto the brand-new row before creation. The broker reuses whichever
  of those are still free and only falls back to fresh ones from the range for any that are not — a server
  the user already pointed a browser at shouldn't silently move to a different port mid-session just
  because the container itself needed to be recreated.
- **Survives a broker restart** the same way container adoption does: on `ListAsync` (the reconciliation
  pass every reconnect runs) and on adopting an already-running container by name in `CreateAsync`, the
  broker reads back the ports it previously published from a `personalai.sandbox.ports` container label
  and seeds the allocator with them, so a restarted broker never hands an already-bound port to a new
  sandbox.
- **The model learns its ports via the `ports` action**, not the tool schema — ports are assigned per
  sandbox (per chat session or run), not fixed, so they can't be baked into the tool's static
  `ParametersSchemaJson`. The tool's top-level description tells the model to call `ports` before starting
  anything that needs to be reachable.
- **The model gets a real `http://host:port` URL, not just port numbers.** `ISandboxHostClient.RemoteIp`
  is the connected broker's own source address — captured once, at WebSocket accept time, from
  `HttpContext.Connection.RemoteIpAddress` (which already reflects a trusted proxy's `X-Forwarded-For`,
  since `Program.cs`'s `UseForwardedHeaders` runs ahead of every controller) and stored on
  `SandboxHostRegistry`'s current connection — not re-read per call, since the registry has no
  `HttpContext` of its own once the handshake completes. `SandboxTool.RenderPorts` builds
  `http://{RemoteIp}:{port}` for each published port; a genuinely IPv6 address is bracketed
  (`http://[2001:db8::1]:51234`) since its own colons would otherwise collide with the port separator,
  and a dual-stack socket's IPv4-mapped-to-IPv6 form (`::ffff:172.17.0.1`) is normalized back down to
  plain IPv4 (`SandboxHostController.NormalizeRemoteIp`) before it's ever stored, so neither the UI nor
  the model sees the confusing mapped form. Falls back to the older generic "bind to 0.0.0.0, reachable
  from outside" guidance only if `RemoteIp` is ever unset — defensive only, since `ports` already requires
  `IsConnected` to reach this point. Also surfaced on `GET /api/sandbox/status` (`RemoteIp`) for the
  `/settings/tools` broker-status card, so a human can see the same address the model was told.
- Publishing is unconditional — there is no per-sandbox opt-out — but it costs nothing when unused: an
  unbound published port is simply not listening on anything.

## Sandbox file browser (human-facing REST surface)

Separate from the model-facing `sandbox` tool: `SandboxFilesController` (`api/sandbox/{ownerId}/files/**`)
gives a human view/edit/download/upload/zip access to the same `/work` directory over plain REST, gated by
`GET api/sandbox/{ownerId}/status` (`SandboxOwnerStatusDto.CanBrowse`) — a pure read the frontend polls to
decide whether to show a "Sandbox files" button on a chat session or an in-progress agent run, never
creating or recreating a container as a side effect.

- **Provisioning is shared but asymmetric.** `SandboxTool`'s get-or-create logic was extracted into
  `SandboxProvisioningService.EnsureAsync` (full get-or-create, unchanged behavior) and a new
  `TryEnsureExistingAsync` (browsing-only: never creates a sandbox that never existed for an owner; reuses
  a `Running` row, transparently recreates a not-Running one the same way the tool does). The one
  asymmetry: an **Ephemeral** (skill/agent-run) sandbox that reached `Destroyed` is treated as permanently
  gone (both execution services call `DestroySandboxAsync(..., deleteFiles: true, ...)` on completion), so
  it's not browsable — but a **Session** (chat) sandbox's `Destroyed` row (the idle reaper retires the
  container but keeps its files) still is, recreating the container transparently on first use.
- **Two new `FileOp` values**, appended (never inserted — the enum serializes as a plain int ordinal, no
  `JsonStringEnumConverter`) to avoid desyncing a deployed broker/API pair: `ReadRaw` (exact byte-for-byte
  read — unlike `Read`, no `File.ReadAllLines`/text-join round trip, which would silently corrupt real
  binary content — fails outright over `MaxDownloadBytes` rather than truncating a download into a corrupt
  file) and `Zip` (builds a `System.IO.Compression.ZipArchive` from either `FileOpRequest.SelectedPaths`
  or everything under `Path`, respecting the same ignored-directories rule `List` uses; sums raw file sizes
  while walking and aborts *before* building the archive if the selection would exceed `MaxZipBytes`).
- **The WebSocket frame-size cap had to move.** `WebSocketFrameTransport`'s inbound cap defaults to 16 MB;
  base64 inflates a payload ~1.33x, so a `Zip`/`ReadRaw` response near the new, larger
  `MaxZipBytes`/`MaxDownloadBytes` caps would exceed it — and an oversized frame doesn't just fail its own
  request, `WebSocketFrameTransport.ReceiveAsync` throws, which `SandboxFramePump`'s reader loop turns into
  faulting *every* pending request and killing the whole connection. `SandboxHostRegistry.MaxFrameBytesFor`
  / `BrokerConnection.MaxFrameBytesFor` (mirrored, same formula) size the transport's cap off
  `max(MaxZipBytes, MaxDownloadBytes) * 1.5 + 1 MB`, floored at the transport's own 16 MB default.
- Content viewing sniffs text vs. binary server-side (`SandboxFilesController`, strict UTF-8 decode with no
  replacement fallback, plus a check for an embedded NUL) against the **full, untruncated** `ReadRaw`
  result — a file cut off mid multi-byte UTF-8 character must not falsely read as binary. Saving from the
  browser's plain-text editor is a full `Write` overwrite, not the model's find/replace `Edit` op, since a
  free-form editor has no "old text" to match against. Upload reuses `Write` unchanged (already
  binary-safe on the wire) and is therefore still bounded by `MaxFileTransferBytes`, not the larger
  download/zip caps — large uploads are a known v1 limitation, not addressed here.

## Approval for dangerous commands

`exec` reuses the exact `ToolApprovalRequired`/`PreApproved` mechanism the in-process file tools already
use for a workspace escape — no new plumbing, since `ToolExecutionResult`, `ChatAgentService`'s pause/
resume loop, `POST /api/tool-invocations/{id}/resolve`, and the frontend's `ApprovalPrompt` are all
tool-agnostic already.

- **`Sandbox:RequireApprovalForDangerousCommands`** (default `true`): before running a command, `exec`
  joins the argv into one string (so both `["pip","install","x"]` and a `/bin/sh -c "..."` string are
  checked the same way) and tests it against **`Sandbox:DangerousCommandPatterns`** — a list of
  case-insensitive regexes covering package-manager install/upgrade subcommands, `sudo`, `rm` with a
  combined recursive+force flag in either order (`-rf`, `-fr`, `-Rf`, `-rfv`, ...), a curl-or-wget piped
  into a shell, a numeric `chmod`, `mkfs`, and `dd if=`. A match returns `ToolApprovalRequired` instead of
  running the command.
- **Operator config only** — deliberately not a live tool setting like `AdditionalAllowedImages`. This is
  a security posture, not a UX convenience, matching `NetworkScan:AllowedRanges`/`HttpTool:AllowedUrls`: a
  list the end user could freely edit away would defeat the point of a safety net they're the one meant to
  be protected from.
- **A heuristic, explicitly not a boundary.** The pattern list cannot catch every dangerous invocation (an
  installer with an unusual name, an obfuscated pipe-to-shell, ...), and once approved the command still
  runs with the sandbox's full, otherwise-unrestricted privileges. It is a speed bump for the common cases
  (installs, `sudo`, `rm -rf`), not a sandbox-within-the-sandbox. A malformed regex an operator adds is
  skipped rather than thrown, so one typo doesn't take the whole check down.
- **`apt`/`apt-get` gets its own pattern**, checked before the generic package-manager one: its `update`
  subcommand only refreshes the package index and installs nothing — a routine command a model runs
  constantly right before `apt-get install` — whereas for every other package manager here `update` *does*
  mean "upgrade installed packages." Folding apt into the generic pattern would flag `apt-get update` for
  approval every single time; only `apt(-get) install|upgrade|dist-upgrade|full-upgrade` is flagged.
- **Chat only, by construction, not by a special case.** `ChatAgentService` pauses the session
  (`SessionStatus.AwaitingApproval`) and surfaces the existing approve/deny UI exactly as it would for a
  file-tool workspace escape. `SkillExecutionService`/`AgentExecutionService` already reject *any*
  `ToolApprovalRequired` outright — no user to ask — so a flagged command in an unattended Skill or Agent
  run is simply refused, with no new code needed to make that true.
- Approved via the same resume path as everything else: `ChatAgentService.ExecutePreApprovedAsync`
  re-invokes the tool with `ToolExecutionContext.PreApproved = true`, which skips the check unconditionally
  — a command is only ever asked about once per invocation, not re-flagged on the approved retry.

## Path collision with the in-process file tools

The sandbox tool's file actions (`read_file`, `write_file`, `edit_file`, `list_files`, `search_files`)
share their exact names with five separate, pre-existing in-process tools confined to `Workspace:RootPath`
(never `/work` in any deployment). A model that has used the sandbox and internalized "paths are relative
to `/work`" can call the **wrong** tool — the standalone in-process one — with a `/work`-rooted path,
since both tools' schemas look equally plausible for "write a file at /work/x.py." `Workspace.Resolve`
correctly identifies `/work/...` as a rooted path outside its own root and — before this fix — returned
the ordinary `ToolApprovalRequired`, pausing the chat for a human to approve something that could never be
right: writing to a literal `/work` directory on the API's own host (a different machine from the sandbox
entirely in most deployments) has no legitimate use.

- **`Workspace.LooksLikeSandboxWorkPath(path)`** — true for any rooted path equal to or nested under
  `/work` (case-insensitive), checked against the model's raw argument, not the resolved absolute path.
  Exposed on `WorkspacePathResolution.LooksLikeSandboxPath` so every tool that calls `Workspace.Resolve`
  gets it for free.
- **Every one of the five in-process file tools checks it first**, before the generic
  outside-workspace approval branch, and **refuses outright** (`ToolCompleted(..., IsError: true)` via the
  shared `Workspace.SandboxPathHint(path)` message) rather than pausing for approval — there is no
  deployment where approving it would do anything useful, so asking wastes a round trip and, worse, trains
  the user to reflexively approve prompts from this tool. The message names the sandbox tool's own
  actions explicitly, which — since it lands back in the model's own conversation history exactly like any
  other tool result — tends to make the model self-correct on its next call rather than repeating the
  mistake.
- Deliberately narrow: only a **rooted** path matching `/work` triggers this. A relative path like
  `work/notes.txt` (a genuine subdirectory named "work" inside the actual workspace) and an absolute path
  that merely shares a prefix (`/workbench/...`) are unaffected and still go through the ordinary
  workspace-escape approval flow if they resolve outside the root.

## Waking and sleeping the Docker host

The broker's own machine — physical or virtual — can't dial in while it's powered off, so the sandbox
tool has two optional webhook settings, **Host start URL** and **Host stop URL**, for whatever the
deployment uses to wake or sleep it (a Wake-on-LAN relay, a smart plug's API, a Home Assistant or n8n
webhook, ...). Both are plain `Url`-type tool settings (`SandboxTool.SettingsFields`, `HostStartUrl`/
`HostStopUrl`), stored in `ToolConfig` exactly like `AdditionalAllowedImages` — live-editable via
`/settings/tools`, no restart needed, and a no-op on both paths with either left unset.

- **`ISandboxHostPowerSignaler`** (`Core/Services/Sandbox`, **registered as a singleton — load-bearing,
  not incidental**, since its debounce state must survive across every call, not reset per DI scope) owns
  both directions. A plain HTTP GET to the configured URL either way; neither response body nor a
  particular status code is expected, since this is meant to work with arbitrary webhook receivers.
- **Start**: `SandboxTool`'s existing "no broker connected" short-circuit is the trigger. It first calls
  `TryFireStartAsync(force: false, ct)`; if no `HostStartUrl` is configured (returns `null`) it fails fast
  with the old generic "not connected" message, exactly as before this feature existed. Otherwise — since
  the fix that shipped after the wake signal alone, without waiting, just left the model erroring out on
  every retry while the host booted — it **blocks and polls** for up to `Sandbox:HostStartMaxWaitSeconds`
  (default 120s; `0` reverts to the old fail-fast behavior), checking `ISandboxHostClient.IsConnected` every
  `Sandbox:HostStartPollIntervalSeconds` (default 5s) and re-firing the start webhook — this time
  `force: true`, bypassing the cooldown — every `Sandbox:HostStartRetryIntervalSeconds` (default 30s) in
  case the first request didn't land. While waiting it reports progress via `ToolExecutionContext.OnProgress`
  ("Waiting for sandbox host to boot..."), which `ChatAgentService`/`SkillExecutionService`/
  `AgentExecutionService` forward as `ToolProgressEvent` → SSE `tool-progress` frames (see "SSE Streaming
  Protocol" in `CLAUDE.md`) so a live chat viewer sees the wait instead of a silent hang. All three services
  run the tool call on a background `Task` and pump progress events off an unbounded `Channel<AgentStreamEvent>`
  concurrently with `await`ing it, since an async-enumerable `yield` can't sit inside the tool's own
  `try/catch`. If the broker still hasn't connected once the deadline passes, the tool returns the same
  kind of `ToolCompleted(..., IsError: true)` as before, now naming the wait duration. Debounced re-fires
  aside, the original cooldown (`Sandbox:HostStartCooldownSeconds`, default 120s) still governs the first,
  non-forced call to `TryFireStartAsync` on each fresh "not connected" tool invocation, so a model retrying
  the tool itself right after a wait timeout doesn't immediately re-hit the webhook either.
- **Stop**: triggered by `SandboxReaperHostedService`'s tick, not by the tool — after each tick's reap
  work has applied, `SignalHostPowerStateAsync` re-queries `ISandboxRepository.GetAllActiveAsync` (its
  "not yet Destroyed" filter deliberately still counts a `Creating`/`Orphaned` row, since either
  represents ongoing intent to use a sandbox); zero remaining fires `TryFireStopAsync`, one or more calls
  `NotifyActiveSandboxObserved` instead. Fires **at most once per idle period** — `TryFireStopAsync` sets
  an internal flag the moment it fires, and only `NotifyActiveSandboxObserved` (i.e. a sandbox becoming
  active again) clears it — so an idle deployment doesn't hit the stop webhook on every `ReapPollSeconds`
  tick indefinitely.
- Both directions swallow their own request failures (logged as a warning, never thrown) — a webhook
  being briefly unreachable must not take down the reaper's tick or fail the sandbox tool call outright;
  `TryFireStartAsync` still surfaces the failure as part of its returned message so the model (and the
  user reading the transcript) knows the wake attempt didn't land.

## Scoping and lifecycle

- **One sandbox per `ChatSession`** (`SandboxKind.Session`) — created lazily on first use, persists across
  turns, retired (not deleted) by the reaper after `Sandbox:IdleTimeoutMinutes` of inactivity, and
  destroyed with its files only if the owning `ChatSession` itself is deleted.
- **One sandbox per Skill run or Agent run** (`SandboxKind.Ephemeral`) — created lazily on first use within
  that run, destroyed with its files in a `finally` as soon as the run ends, or by the reaper's
  `EphemeralGraceMinutes` safety net if the process died before the `finally` could run. A nested Skill
  invoked from within an Agent run gets its own independent ephemeral sandbox, not a shared one.
- **Recreation is not silent to the model.** Whether recreating after `Orphaned` or after the idle reaper's
  retirement above, the next tool call for that owner returns a result prefixed with a `[Sandbox note]`
  telling the model the container is fresh, any process it started in an earlier turn is gone, and listing
  the current ports — see `SandboxTool.BuildRecreationNotice`, wired through the `WasRecreated` flag
  `GetOrCreateSandboxAsync` returns. Not shown on a plain first-ever creation for an owner, since nothing
  was running before and the notice would be noise.
- **Session flag**: `ChatSession.SandboxEnabled`, default `false` — the same opt-in posture as
  `NetworkScanEnabled`/`ComputerControlEnabled` — gating the tool via `ChatAgentService.DisabledToolNames`.

### Mid-call self-heal on a dead container

`GetOrCreateSandboxAsync`/`EnsureAsync` only recreates when the DB row's `Status != Running` — it never
re-verifies a `Running` row against reality on its own. Since `ReconcileAsync` only runs at broker
(re)connect, a container that dies while the broker connection stays up (killed manually, OOM'd, the host
rebooted without the broker process itself restarting) leaves the row falsely `Running` until something
else notices. Rather than adding a periodic liveness poll, the broker itself detects this at the point of
failure: `DockerSandboxManager.ExecAsync` classifies a Docker 404 (container removed) or 409 (exists but
not running) — and the "no cached or resolvable record at all" case for both `exec` and every file op — as
`ContainerMissing: true` on the response frame (`ExecCompleted`/`FileOpResponse`), instead of a bare error
string. `SandboxTool` reacts to that signal by marking the row `Orphaned`, recreating the container via
`SandboxProvisioningService.RecreateAfterContainerMissingAsync` (preserving ports, same as any other
recreation), retrying the exact same `exec`/file-op call once against the fresh container, and folding the
same `[Sandbox note]` notice into that call's result — one extra broker round trip, entirely inside the
one tool-call turn, no extra turn back to the model. A retry that also reports `ContainerMissing` is
surfaced as a real error instead of retried again — this heals exactly once per call, not in a loop.
Deliberately reactive-only: nothing proactively re-verifies an idle `Running` row that no tool call has
touched in a while, since the next call to touch it will self-heal regardless.

This is also what fixed a related silent failure: a hard exec failure (container missing and the retry
above also failing, or any other broker/Docker error with no exit code) used to render to the model as
`Exit code: unknown` with empty stdout/stderr and no error flag, because `ExecCompleted.Error` was captured
on the wire but never read on the way into `SandboxExecResult`. It's now surfaced as an explicit tool error
with the real message.

## Configuration

- **`Sandbox`** (`Core/Models/Settings/SandboxSettings.cs`, section `"Sandbox"`): `Enabled`, `HostKey`
  (empty disables the endpoint entirely — must match the broker's `Broker:HostKey`/`SANDBOX_HOST_KEY`
  exactly), `DefaultImage`, `AllowedImages`, `RequireApprovalForDangerousCommands` (default `true`) and
  `DangerousCommandPatterns` (see "Approval for dangerous commands" above), `HostStartCooldownSeconds`
  (default 120), `HostStartPollIntervalSeconds` (default 5), `HostStartMaxWaitSeconds` (default 120, `0`
  disables the wait), `HostStartRetryIntervalSeconds` (default 30 — see "Waking and sleeping the Docker
  host"; the two URLs themselves are tool settings, not here), exec/output/heartbeat/idle/reap timeouts and
  caps, and a set of resource/hardening knobs
  (`CpuLimitMilliCores`, `MemoryLimitBytes`, `PidsLimit`, `RunAsNonRoot`, `CapDrop`) that default to
  null/false — unlimited and permissive — since
  tightening them was explicitly deferred rather than requested.
- **`Broker`** (the broker's own `BrokerSettings`): `ApiUrl`, `HostKey`, `HostId`, `SandboxRootPath`
  (default `/data/sandbox`), `DockerEndpoint`, `ExposedPortCount` (default 5),
  `HostPortRangeStart`/`HostPortRangeEnd` (default 20000–29999), plus its own copies of the
  output/list/search/transfer caps — it can't reference Core's `SandboxSettings`, since referencing Core
  would pull in EF Core, FlaUI, and `System.Drawing.Common`.
- **`docker-compose.yml`** — a fifth service, `sandboxhost`, mounting `/var/run/docker.sock` and the
  sandbox root at the **identical path** on both sides of the bind mount (`HostConfig.Binds` is resolved
  by the Docker daemon using the host's own view of the path, so a mismatch here silently mounts an empty
  directory into every sandbox). `SANDBOX_HOST_KEY` is forwarded to **both** the `api` service (as
  `Sandbox__HostKey`) and the `sandboxhost` service (as `Broker__HostKey`), since it is one shared secret
  compared across the WebSocket upgrade — an early pass wired only the broker half, which would have left
  the API rejecting every connection with a 401 while the broker retried forever.
- **Remote Docker host** — when the API runs somewhere other than the Docker host, the broker runs on the
  Docker host under systemd (`personalai-sandboxhost.service`) and dials out to the API's public URL. Any
  reverse proxy in front of the API must forward the WebSocket `Upgrade`/`Connection` headers for
  `/api/sandbox/host` — worth checking first if the broker never connects.

## Security posture

- The shared host key is root-equivalent on the Docker host (whoever holds it can ask the broker to
  create, exec, or destroy arbitrary containers) and is treated like `Auth:JwtKey` — plaintext in local
  `appsettings.json`, an environment variable or secret store in deployment.
- `SandboxPathResolver`'s symlink resolution is the load-bearing security fix in this feature, verified
  against a real Linux container in both directions.
- Resource limits and process hardening (CPU/memory/pids caps, non-root, dropped capabilities) exist as
  configuration but default off/unlimited; turning them on is a config change, not a code change.
- Network access from inside a sandbox container is on by default (`Sandbox:NetworkEnabled`) — deliberate,
  since installing packages requires it.

## Known gaps / open items

- No multi-root / additional-mount-point support yet — a sandbox can only see its own owner directory at
  `/work`. See `SANDBOX_ENHANCEMENT.md` for the considered design (an allow-listed list of
  `{ hostPath, containerPath, readOnly }` mounts) for when a concrete need for broader access arises.
- The API can only ever hold one broker connection; a second broker, or a future multi-replica API, is
  unsupported today. `ISandboxHostClient` is kept as a narrow seam specifically so this could be swapped
  later (e.g. a Redis-relay implementation) without touching `SandboxTool`.
- **A large Zip/ReadRaw send can starve the heartbeat and take the connection down mid-transfer.**
  `SandboxFramePump` has exactly one writer loop draining one outbox `Channel<SandboxFrame>` per
  connection (see [Wire protocol summary](#wire-protocol-summary)) — a large frame being sent blocks
  everything else queued behind it, including a `Heartbeat` reply. At the default `MaxZipBytes` (50 MB,
  ~67 MB base64) and `Sandbox:HeartbeatTimeoutSeconds` (45s), a zip transfer slower than roughly
  1.5 MB/s end-to-end can miss a heartbeat and get the whole connection torn down — not just that one
  request, every other in-flight sandbox operation on the same broker. Not fixed here: a proper fix needs
  either a second, priority outbox channel for control frames or chunked streaming of large `FileOp`
  payloads (the same "known gap" the original sandbox design already flagged for exec output at a much
  smaller scale). Until then, an operator whose broker sits behind a slow link should raise
  `Sandbox:HeartbeatTimeoutSeconds` (and its `Broker:` counterpart) or lower `Sandbox:MaxZipBytes`/
  `MaxDownloadBytes` to keep worst-case transfer time comfortably under the heartbeat timeout.
- `SandboxFilesController`'s file-browser upload endpoint reuses `FileOp.Write` unchanged, so it stays
  capped at `MaxFileTransferBytes` (5 MB default) rather than the larger `MaxDownloadBytes`/`MaxZipBytes` —
  a large upload is a known v1 limitation, not addressed here.
- **Mid-call self-heal (see "Mid-call self-heal on a dead container" above) is deliberately reactive-only.**
  A container that dies without any tool call touching it afterward stays `Running` in the DB and
  unreachable until the next call finally hits it and self-heals — no periodic re-verification exists. In
  practice a chat session's next message naturally triggers a tool call, so this only matters for a
  sandbox nobody is actively using; adding a broker-side or reaper-driven periodic liveness check was
  considered and deferred as unnecessary complexity for that case.

## Testing

`tests/PersonalAI.Tests/Sandbox/`, `tests/PersonalAI.Tests/SandboxHost/`, `tests/PersonalAI.Tests/Tools/`,
and `tests/PersonalAI.Tests/Controllers/` cover: frame round-trips through the polymorphic base type;
request/response correlation and chunk ordering over a fake in-memory transport; duplicate-connection
handling; the three reconciliation outcomes; every one of the ten tool actions against a mocked
`ISandboxHostClient`, including the two-source image allow-list (operator config union tool setting,
dedup, trim, empty-means-any), preferred-port reuse on recreation, and the dangerous-command approval
gate (every default pattern including all four `rm` flag orderings, the shell-string form, the
approved-retry path, the disabled-by-config path, a malformed operator pattern being skipped rather than
thrown, and — the one an earlier pass got wrong — that `apt-get update`/`pip list`/`npm ls` do NOT
false-positive just because they share a verb with a dangerous package-manager subcommand);
`SandboxPortAllocator`'s selection logic (preferred-port reuse, range fallback, seeding, exhaustion)
against an injected availability predicate, with no socket or Docker daemon needed; path-escape rejection
including the symlink case (skipped, not silently passed, on a machine that can't create symbolic links —
verified separately against a real Linux container); reap candidate selection for all three retention
cases; and — in `tests/PersonalAI.Tests/Tools/` — `WorkspaceTests`, `ReadFileToolTests`,
`WriteFileToolTests`, and `SandboxPathHintTests` (new, covers `EditFileTool`/`ListFilesTool`/
`SearchFilesTool`, which have no dedicated test file of their own) cover the sandbox-vs-workspace path
collision above: the `/work` detection itself, that a genuine `work/`-named workspace subdirectory is
unaffected, and that every one of the five in-process file tools refuses outright rather than requesting
approval; `SandboxHostRegistryTests` covers `RemoteIp` (reflects the connected broker's address, null
before/after connection, updates on supersession); `SandboxHostControllerTests` covers
`NormalizeRemoteIp`'s IPv4/IPv4-mapped-IPv6/genuine-IPv6 cases directly, with no WebSocket handshake
needed; `SandboxToolTests` covers `ports`' URL rendering (a real `http://host:port` per port, IPv6
bracketed, falls back to generic guidance when `RemoteIp` is unset); `SandboxHostPowerSignalerTests`
covers both webhook directions against a fake HTTP handler — start/stop no-ops with nothing configured,
the start cooldown (advanced via a `FakeTimeProvider`, not a real clock), the stop debounce and its reset
on `NotifyActiveSandboxObserved`, and an endpoint error or a malformed URL producing a message rather than
a thrown exception; and `SandboxReaperHostedServiceTests` covers `SignalHostPowerStateAsync` against a
real (InMemory) database — zero active sandboxes fires the stop signal, one of any non-`Destroyed` status
(`Running`, `Creating`, or `Orphaned`) notifies active instead. `SandboxProtocolTests` also round-trips
`ExecCompleted`/`FileOpResponse` with `ContainerMissing: true` and covers the wire backward-compat case
(JSON omitting the field deserializes to `false`); `SandboxProvisioningServiceTests` covers
`RecreateAfterContainerMissingAsync` (active row recreated and ends `Running` with ports preserved, no
active row returns not-recreated without touching the broker, and — the critical case — the host client
throwing during recreation leaves the row `Orphaned` rather than falsely `Running`); `SandboxToolTests`
covers the mid-call self-heal end to end for both `exec` and a file op (`ContainerMissing` then success on
retry gets a `[Sandbox note]` and exactly one retry; `ContainerMissing` on the retry too surfaces a real
error without looping; a file-op retry uses a fresh `RequestId`, not the first attempt's), plus the
independent fix for a hard exec failure that isn't a container-missing report (`ExitCode: null`,
`ContainerMissing: false`) now surfacing as a real error instead of a silent `Exit code: unknown`. As of
this writing: 511 passing, 3 skipped (the symlink tests, which require a Linux host to create real
symlinks).

Two things Docker.DotNet-dependent code can't unit-test are verified manually and separately, not by
`dotnet test`: the exact `EnsureImageAsync` pull path (`InspectImageAsync` throws
`DockerImageNotFoundException` → `CreateImageAsync` pulls → a subsequent inspect succeeds), run against a
real daemon with the target image forcibly removed first; and port publishing end-to-end — a container
created with `ExposedPorts`/`HostConfig.PortBindings` for chosen ports, a Python HTTP server started
inside it via `docker exec`, and a real HTTP request from outside any container reaching it on the
published port and getting a 200. The same applies to `DockerSandboxManager.ExecAsync`'s 404/409 →
`ContainerMissing: true` classification and its `_sandboxes` cache eviction, and `BrokerConnection`'s
equivalent for file ops — no mockable seam exists for the real `DockerClient` today, so these are verified
manually against a real daemon (`docker kill`/`docker rm` a sandbox container mid-session, then exec/read a
file against it and confirm the model gets a working sandbox back with a clear note, not a silent
`Exit code: unknown`) rather than by a unit test.
