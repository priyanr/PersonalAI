# Sandbox Architecture — Alternatives Considered

This records a design discussion held right after the sandbox tool (see
[`SANDBOX_ARCHITECTURE.md`](SANDBOX_ARCHITECTURE.md)) was implemented and reviewed. The user proposed
three variations on the execution model; each is recorded here as prompt + response, followed by the
resulting recommendation. Nothing in this file was implemented — it's a decision record, not a spec.

---

## Round 1 — copy the broker into the container, let it dial the API itself

**Prompt:**

> I think we should have done one things once container copy our broker with docker cp to container this
> instance also connect to our api so broker itself can execute everything and host same process will run
> container copy broker and execute broker think do not make any changes

**Response:**

Read as: instead of one host-side broker driving every container from outside via the Docker API, `docker
cp` the broker binary into each sandbox container and let that in-container instance dial the API
directly — one WebSocket per running sandbox instead of one for the whole host.

Where it looks better:
- Exec becomes a plain in-process `Process.Start` with normal stdio pipes — no `Docker.DotNet` exec/attach
  API, no `MultiplexedStream` demuxing, no `InspectContainerExecAsync` call for the exit code.
- File operations become plain local I/O inside the container's own filesystem — no cross-boundary path
  resolution, so no class of "does this path resolve outside the bind mount" bug at all.

Where it doesn't hold up:
- **It doesn't remove the host-side process, it only shrinks its job.** Something outside every container
  still needs to receive "create a sandbox for this owner" from the API and do the `docker
  create`/`cp`/`exec`-to-launch — that's still a host-level broker holding an outbound WebSocket and still
  needing the Docker exec API, for the one call this idea was meant to avoid.
- **The connection model gets harder.** `SandboxHostRegistry` is built around exactly one connection today.
  This makes it N+1 — one per launcher, one per live sandbox — each needing its own auth and
  reconnect/heartbeat handling, plus a routing table from sandbox ID to the right socket. That's a bigger
  rewrite of the API-side registry than anything this saves on the broker side.
- **Security regression.** The API host key never enters a container today — it's held by one process
  entirely outside the blast radius. For each sandbox to dial the API itself, either that same key or a
  derived credential has to live inside the container, next to the model's own exec'd commands. Anything
  the model runs can read that binary's environment or memory. That's not a tuning knob, it's handing the
  sandboxed workload a credential that can talk to the API's most privileged internal endpoint.
- **"Vanilla images" stops being true.** A copied self-contained binary has to match the container's
  OS/libc/architecture — glibc vs musl (Alpine), amd64 vs arm64, no Windows containers at all. You'd need
  a broker build per target and logic to pick the right one at copy time. That's the same "custom image
  per runtime" cost the host-broker design was chosen specifically to avoid, just moved from build-time to
  copy-time.
- **Recovery still needs the exec fallback anyway.** If the in-container broker dies (OOM, or the model
  kills it), the only way back is the host process re-copying and re-execing it — so the "avoid Docker
  exec" savings evaporate the moment anything goes wrong, which for arbitrary model-driven code isn't an
  edge case.

**Verdict:** not recommended. The stdio simplicity is real but narrow, and it costs a credential inside
the attacker-adjacent container plus the loss of true vanilla-image portability.

---

## Round 2 — in-container process hosts a WebSocket server; the host broker connects into it

**Prompt:**

> Think one more same way but I side another process runs one web socket host process connect to it and
> sends commands over web socket think don't make any changes

**Response:**

Read as: keep the host-side broker as the *only* thing that talks to the API. The in-container piece
(still `docker cp`'d in) instead runs a WebSocket **server**; the host broker connects into it as a
client and proxies exec/file-op requests down, relaying results back up over its one connection to the API.

Where this is a genuine improvement over Round 1:
- **The API host key never enters a container.** Only the host-side broker holds it. Each in-container
  agent just needs a small per-sandbox local token to authenticate the broker's connection to it — worthless
  to anyone outside that one container, since they already have root inside it. The strongest objection to
  Round 1 is gone.
- **The API side is completely untouched.** `SandboxHostRegistry`, the protocol project, `SandboxTool`,
  every test — none of it changes. Only `PersonalAI.SandboxHost`'s internals would change: instead of
  `DockerSandboxManager` calling Docker's exec/attach API directly, it opens a second hop into the
  container and proxies the same frames down. The existing `SandboxFramePump` and `WebSocketFrameTransport`
  could be reused verbatim for that second hop — no new protocol design needed.
- **File I/O moves inside the container's own namespace.** A resolver bug there now costs the container's
  own files, not the host's, because the kernel's namespace isolation becomes the actual backstop instead
  of application-level path-validation code.

Where it still doesn't hold up:
- **Portability is still unsolved — the disqualifying one.** The in-container binary still has to run
  inside whatever image the model picked. Hosting a server instead of dialing out changes nothing here.
- **A second WebSocket server implementation has to be built.** Nothing in this codebase hosts a WS server
  outside the main API today; the broker is purely a `ClientWebSocket` consumer. A slim, trimmed,
  self-contained server that has to run correctly inside anything from `python:3.12-slim` to
  `node:22-slim` is new engineering, not a simplification.
- **A second reconnect state machine.** If the broker-to-container link drops but the container is still
  alive, that's a distinct failure mode needing its own detection/retry — essentially duplicating the
  pump's reconnect story a second time, inside the same process, for a second purpose.
- **More Docker networking.** The host broker needs to reach each container's internal IP:port, meaning a
  shared user-defined bridge network and container-inspect-to-get-IP logic that doesn't exist today.

**Verdict:** better than Round 1 specifically on the credential-exposure axis, and the namespace-isolation
point is a real security upgrade. But it doesn't solve the actually disqualifying problem (portability),
and it trades already-built, already-tested code (Docker exec/attach, the now-hardened path resolver) for
new engineering (a WS server, a second reconnect loop, network wiring). A cheaper way to get the same
namespace-isolation benefit without any of that: shell out to `docker exec <container> sh -c "<cmd>"` as a
subprocess with argument arrays (never a shell string), from the existing host broker, instead of driving
`Docker.DotNet`'s `MultiplexedStream` API directly. Same result — command runs natively inside the
container's namespace — without a second protocol, server, or reconnect loop.

---

## Round 3 — the real motivation: future non-sandboxed paths, plus a controlled base image

**Prompt (final, after an interrupted first attempt with the same substance):**

> My problem is io I may need this to write non sandboxed paths later think about simple dotnet or python
> process inside to execute commands we will make a base image with dontnet and python installed

**Response:**

This changes the calculus in a real way, but it bundles two separate questions that need different
answers.

**Owning the base image removes the portability objection.** If sandboxes always run from a base image
built and controlled by this project (dotnet + python baked in), the biggest cost from Round 1/2 — matching
an in-container binary to whatever arbitrary image the model picked — mostly disappears. Publish the agent
once, for one known target, and bake it in at image-build time or copy in something small and
framework-dependent. Worth treating as a real v2 option later, done properly. Worth naming the trade,
though: the original design point of "sandbox images stay vanilla, no rebuild per runtime" gets given up
in exchange. That's a fine trade if a controlled, predictable environment is wanted — just not a free
simplification.

**"I may need non-sandboxed paths later" is a different problem, and deserves a different answer.** The
instinct "let execution happen inside the container so the boundary isn't strictly enforced" plus "I'll
need broader access later" combine into "loosen the jail now so it's easier to widen later" — which is the
same shape as the symlink bug that was just found and fixed. The fix there wasn't "stop checking paths," it
was "check correctly."

The actual lever wanted here is **multiple allowed roots, not one.** A sandbox today gets exactly one bind
mount (`/data/sandbox/<ownerId>` mapped to `/work`) and the path check has exactly one boundary to
enforce. Nothing stops that from becoming a *list* — declare N mounts at container-create time
(`{ hostPath, containerPath, readOnly }`), each one deliberately granted, and the path check validates
against whichever declared root a given path falls under. Docker's bind mounts are already doing the real
enforcement either way; this just teaches the authorization code about more than one of them. That gets
"write to other paths later" as an explicit, audited grant — a shared cache, a specific host folder,
whatever comes up — without ever saying "trust the container boundary, not the check."

Critically, this is **orthogonal to where exec runs.** Whether commands execute via the host broker's
Docker exec API or an in-container process, the mount list and the path check work the same way. Moving
execution inside the container is not required to get broader file access — those are two independent
decisions that arrived bundled together.

**Where this leaves things:**
- For file-access flexibility: extend the resolver to a multi-root model when a concrete need for it shows
  up. Don't loosen the current single-root check preemptively.
- For simpler exec: a small dotnet or python process inside the container, launched via `docker exec`
  against a project-controlled base image, is a reasonable v2 direction now that the portability tax is
  gone — worth scoping properly if/when the base image is built.
- Don't do both by weakening the boundary that's currently correct.

---

## Net recommendation

Keep the architecture in `SANDBOX_ARCHITECTURE.md` as-is for now. Neither round produced a strict
improvement once the actual costs were priced in — Round 1 traded a solved problem for a real credential
leak, Round 2 traded solved problems for new unsolved engineering, and Round 3's genuine insight (own the
base image) answers a portability objection to a redesign that hasn't been justified by a concrete need
yet. The one actionable idea worth carrying forward is the **multi-root mount model** for
`SandboxPathResolver`, to be designed when there's a real requirement driving it rather than in
anticipation of one.
