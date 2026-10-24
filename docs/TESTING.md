# Testing

**Read only if adding or maintaining tests.** 

## Running Tests

```bash
dotnet test PersonalAI.slnx
```

**616 tests** across all subsystems. Runs in xUnit + Moq + FluentAssertions.

## Test Structure

Tests are organized under `tests/PersonalAI.Tests/` by subsystem:

| Subsystem | Coverage |
|-----------|----------|
| `Agent/` | Chat agentic loop (tool calls, approval flow, error recovery, history trimming, iteration cap) |
| `Controllers/` | All API controllers (HTTP contract + status codes) |
| `Infrastructure/` | EF Core, database, migrations |
| `Services/` | ChatAgentService, SkillExecutionService, AgentExecutionService, providers, OAuth, scheduling, search |
| `Tools/` | File/network/HTTP/Gmail/computer/sandbox tool execution; workspace sandboxing; dangerous-command patterns |
| `Sandbox/` | Frame protocol round-trips, request/response correlation, chunk streaming, broker connection state |
| `SandboxHost/` | Path resolver security, file operations, port allocator |

## Key Test Areas

### Chat Agentic Loop (`ChatAgentServiceTests`)

- **Tool calls + results**: model emits tool call → tool executes → result appended to history → loop continues
- **Approval flow**: file op outside workspace → returns `ToolApprovalRequired` → session paused → user approves/denies → turn resumes or terminates
- **Error recovery**: tool execution fails → error result fed back to model → loop continues
- **History trimming**: long conversations trigger compaction (see `COMPACTION_ARCHITECTURE.md`)
- **Iteration cap**: loop exits after `Agent:MaxIterations` calls (default 25)
- **Thinking preference**: `think` is `bool?` — session passes on/off, skills/agents/compaction pass `null` (provider default)
- **Empty/truncated replies**: reply with no text and no tool call is surfaced, not swallowed; partial text kept with a notice

### AI Provider Abstraction (`AIProviderRegistryTests`, provider-specific tests)

- **Registry**: resolves provider by key, loads config from DB
- **Config cache**: in-memory mirror of DB; reloaded on write
- **OpenAI tool-call delta reassembly**: streaming chunks are coalesced into complete tool calls
- **Ollama**, **OpenAI**, **DeepSeek**: each provider's model/chat/pull endpoints

### Skills & Agents (`SkillExecutionServiceTests`, `AgentExecutionServiceTests`)

- **Tool allowlisting**: tool outside allowlist → rejected before execution
- **Secret substitution**: `{{secret.KEY}}` placeholders swapped for real values immediately before tool execution
- **Secret allowlisting**: skill can only read secrets in its allowlist
- **Approval gate**: approval-requiring tool → rejected with explanatory result (no pausing for unattended runs)
- **Scheduled agents**: cron/interval schedule computation, `NextRunAt` lookup, poll-based trigger
- **Run history**: each run recorded with transcript (assistant text + skill invocations)

### Secrets Vault (`SecretsServiceTests`, `SecretsRepositoryTests`)

- **Create/read/update/delete**: CRUD operations on key/value pairs
- **No echo-back**: `GET /api/secrets` returns keys only, never values (only `HasValue` flag)
- **Duplicate key rejection**: creating a key that already exists returns `409`

### Tool System

**File tools** (`ReadFileToolTests`, `WriteFileToolTests`, `EditFileToolTests`, `SearchFilesToolTests`):
- Read operations inside workspace → no approval
- Write operations outside workspace → approval required
- Path resolution safety (no `..`, no absolute paths without approval)
- Edit string replacement counts
- Recursive search caps

**Sandbox tool** (`SandboxToolTests`):
- All ten actions (exec, file ops, directory ops, ports)
- Image allowlist enforcement + preferred-port reuse
- Dangerous-command approval gate (default patterns, approved-retry path, config opt-out)
- Exit code formatting, per-stream output truncation
- Disconnected-host short-circuit
- URL rendering for published ports

**Network tool** (`NetworkScanToolTests`):
- Scope restriction to RFC1918 private ranges
- ICMP/TCP/UDP/HTTPS parallel scanning

**HTTP tool** (`HttpToolTests`):
- URL allowlist enforcement (glob patterns)
- Response body cap + truncation

**Gmail tool** (`GmailToolTests`):
- OAuth token store/refresh (via `GmailOAuthClient`)
- Send/search/read operations

**Computer control** (`ComputerControlToolTests`):
- Screenshot capture + UI Automation element tree (text representation, not image)
- Mouse/keyboard/cursor operations with explicit coordinates

### Workspace Sandboxing

**Path safety** (`SandboxPathResolverTests`, `WorkspaceTests`):
- Paths inside root execute without approval
- Paths outside root require approval
- `..` and absolute paths rejected
- Symlink escapes blocked
- `/work` collision prevention (sandbox tool's mount point)

**Sandbox protocol** (`SandboxProtocolTests`):
- Frame polymorphic discriminator round-trips
- RequestId correlation
- Chunk streaming (concat on receive)
- ContainerMissing backward-compat default
- Graceful degradation on malformed frames

**Broker connection** (`SandboxHostRegistryTests`):
- One persistent WebSocket to `PersonalAI.SandboxHost`
- Duplicate connection supersession (new connection closes old)
- `IsConnected` transitions (connecting → connected → disconnected)

**Sandbox lifecycle** (`SandboxReaperHostedServiceTests`, `SandboxProvisioningServiceTests`):
- Creation/execution/destruction via broker
- Idle timeout detection + reaping
- Orphaned container recovery
- Mid-call self-heal (ContainerMissing → recreation)
- Per-owner directory isolation + permissions

### Background Services

**Agent scheduler** (`AgentSchedulerHostedServiceTests`):
- Polls due agents every `PollIntervalSeconds`
- Fires scheduled agents (Once, Interval, Cron)
- Persists `NextRunAt` correctly

**Chat turn runner** (`ChatTurnRunnerTests`):
- Background execution (not on request)
- SSE subscription + late-subscriber replay (token coalescing)
- Tab close/reload doesn't stop the turn
- One turn per session (second send → `409`)
- Stop button cancels the turn
- Finalization on failure/stop/shutdown (interrupted tool results, partial text, notice)

### OAuth2 / Delegated Auth (`OAuthClientTests`, `AuthControllerTests`)

- **Authorization code flow**: PKCE challenge → authorization endpoint → code exchange
- **Token refresh**: refresh token rotation (single-use)
- **Revocation**: revoke endpoint (RFC 7009)
- **Introspection**: check token validity (RFC 7662)
- **JWT validation**: signature, issuer, audience, client id checks
- **End-session**: OIDC end-session URL (provider logout)

### Database & Migrations

- EF Core DbContext initialization
- Migration application (auto on startup via `db.Database.Migrate()`)
- Entity relationships (ChatSession → ChatMessages, Agent → AgentRuns, etc.)

## Test Infrastructure

| Tool | Purpose |
|------|---------|
| **xUnit** | Test framework |
| **Moq** | Mocking |
| **FluentAssertions** | Readable assertions |
| **EF Core InMemory** | Fake database (fast, no external deps) |
| **FakeTimeProvider** | Mock `ITimeProvider` for scheduler/timeout tests |
| **Fake HTTP handler** | Mock provider calls, OAuth endpoints, mail server |
| **Fake `IFrameTransport`** | Mock WebSocket for sandbox protocol tests |

## Sandbox Testing Without Docker

All sandbox tests use fake/mocked infrastructure — **no real Docker daemon required** for CI:

- `SandboxFramePump` tested against fake `IFrameTransport`
- `SandboxHostRegistry` / `SandboxReconciler` tested in-memory
- `SandboxPathResolver` tested against real temp directory (path safety, no symlink escape)
- `SandboxFileService` tested against real temp directory (byte-exact binary I/O, cap enforcement)
- `SandboxPortAllocator` tested against fake availability predicate (no real sockets)
- `DockerSandboxManager` (real Docker calls) is NOT covered in CI — verified manually against a real daemon

## Coverage

Run with coverage report:

```bash
dotnet test PersonalAI.slnx --collect:"XPlat Code Coverage"
```

**Not covered by this suite** (requires manual verification against a live Docker daemon):
- Actual broker-to-Docker I/O (`DockerSandboxManager` against a live socket)
- Container lifecycle (image pull, create, exec, destroy)
- Network stack (actual port binding, HTTP connectivity from outside containers)

---

See test files (`tests/PersonalAI.Tests/**/*Tests.cs`) for the complete test suite. Do not read this file unless you're adding or maintaining tests.
