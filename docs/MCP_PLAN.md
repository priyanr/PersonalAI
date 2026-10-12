# PersonalAI Sandbox MCP Server

## Overview

Expose PersonalAI's sandbox infrastructure as an MCP (Model Context Protocol) server, enabling Claude, ChatGPT, and other MCP-compatible LLMs to execute code in your personal Docker containers.

**Goal**: Enable any LLM to access your personal sandbox, breaking free from platform-specific integrations.

---

## The Problem

Today:
- Claude in Claude Code has browser automation + computer control (MCP tools), but no access to your personal sandbox
- ChatGPT can execute code in Codex (hosted), but not your infrastructure
- PersonalAI's sandbox is locked behind PersonalAI's own chat interface
- Each LLM platform requires separate integrations

Result: **Code execution is provider-locked and centralized**, not personal.

---

## The Solution: PersonalAI.SandboxMCP

A standalone MCP server that:
1. Listens on stdio or HTTP (MCP standard)
2. Accepts sandbox operation requests from any MCP client (Claude, ChatGPT, etc.)
3. Proxies them to PersonalAI.Api's sandbox endpoint
4. Returns results in MCP format
5. Handles auth, streaming, and error cases

### Architecture

```
Claude / ChatGPT / Gemini (any MCP client)
  ↓ (MCP Protocol: stdin/stdout or HTTP)
PersonalAI.SandboxMCP
  ↓ (HTTP + WebSocket)
PersonalAI.Api/sandbox
  ↓
PersonalAI.SandboxHost (broker)
  ↓
Docker containers on your infrastructure
```

---

## Exposed Tools

The MCP server declares 8 tools (mirroring `sandbox` tool operations):

| Tool | Args | Returns | Notes |
|------|------|---------|-------|
| `sandbox/exec` | `cmd`, `workDir?`, `timeout?` | `stdout`, `stderr`, `exitCode` | Execute a command |
| `sandbox/read_file` | `path`, `offset?`, `limit?` | `content` | Read file contents |
| `sandbox/write_file` | `path`, `content` | `bytesWritten` | Create/overwrite file |
| `sandbox/edit_file` | `path`, `oldString`, `newString` | `bytesChanged` | Find-and-replace |
| `sandbox/list_files` | `path`, `recursive?` | `files[]` | Recursively list |
| `sandbox/search_files` | `path`, `pattern` | `matches[]` | Regex search |
| `sandbox/delete` | `path` | `deleted` | Delete file/dir |
| `sandbox/move` | `src`, `dst` | `moved` | Move/rename |
| `sandbox/mkdir` | `path` | `created` | Create directory |
| `sandbox/ports` | *(none)* | `ports[]` | List published ports |

Each tool declares its parameters as JSON Schema (MCP standard).

---

## Implementation Plan

### Phase 1: Skeleton

**New project**: `src/PersonalAI.SandboxMCP/` (.NET 10, Console App)

```csharp
// Program.cs
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddSingleton<SandboxMcpServer>()
    .AddHttpClient<PersonalAiClient>(cfg => {
        cfg.BaseAddress = new Uri(builder.Configuration["PersonalAi:ApiUrl"]);
        cfg.DefaultRequestHeaders.Authorization = new(
            "Bearer", builder.Configuration["PersonalAi:ApiKey"]
        );
    });

var host = builder.Build();
await host.Services.GetRequiredService<SandboxMcpServer>().RunAsync();
```

**Configuration** (`appsettings.json`):
```json
{
  "PersonalAi": {
    "ApiUrl": "http://localhost:5080",
    "ApiKey": "your-bearer-token",
    "SandboxSessionId": "chat-session-123"
  },
  "Mcp": {
    "Transport": "stdio"  // or "http" for server mode
  }
}
```

### Phase 2: MCP Server Logic

**SandboxMcpServer.cs**: 
- Implements MCP server lifecycle (initialize, list tools, call tool)
- Declares tools with JSON Schema
- Routes tool calls to PersonalAiClient

**PersonalAiClient.cs**:
- HTTP client to PersonalAI.Api
- Methods: `ExecAsync`, `ReadFileAsync`, `WriteFileAsync`, etc.
- Converts PersonalAI `ToolExecutionResult` → MCP response

**Transport**:
- **Stdio mode** (default): Use `Microsoft.Extensions.MCP` or similar library
- **HTTP mode** (optional): Expose `/mcp/tools`, `/mcp/call` endpoints

### Phase 3: Integration

**Deployment**:
1. Build: `dotnet publish src/PersonalAI.SandboxMCP -c Release -o dist/sandboxmcp`
2. Run: `./dist/sandboxmcp/PersonalAI.SandboxMCP` (or as systemd service)

**Claude Code integration** (`settings.json`):
```json
{
  "mcpServers": {
    "personalai-sandbox": {
      "command": "path/to/dist/sandboxmcp/PersonalAI.SandboxMCP"
    }
  }
}
```

Then use in Claude:
```
/mcp add personalai-sandbox
```

### Phase 4: Testing & Hardening

- Unit tests: tool declaration, parameter validation
- Integration tests: mock PersonalAI.Api responses
- E2E test: real PersonalAI instance
- Error cases: auth failure, sandbox not available, timeout, etc.

---

## Authentication

**Options**:

1. **Bearer token** (simplest):
   - PersonalAI.Api returns a session token when the MCP server connects
   - Token is passed in every subsequent request
   - **Pro**: Simple, stateless
   - **Con**: Token must be rotated

2. **API key** (alternate):
   - PersonalAI.Api issues a long-lived API key
   - MCP server includes it in `Authorization: Bearer` header
   - **Pro**: Reusable across sessions
   - **Con**: Needs key management

3. **OAuth2** (future):
   - MCP server redirects to auth flow
   - **Pro**: Secure, delegated
   - **Con**: Complex for CLI tool

**Recommendation**: Start with **Bearer token** (option 1), stored in MCP server config.

---

## Existing Alternatives (for context)

| Project | Model | Access | Cost | Privacy |
|---------|-------|--------|------|---------|
| **E2B** | OpenAI, Claude plugins | Hosted | $$ | Shared |
| **Claude computer_control** | Claude native | Local machine only | Free | Local |
| **Replit Agent** | Replit API | Replit environment | Free tier | Shared |
| **PersonalAI.SandboxMCP** | Any MCP client | Your infrastructure | Free (self-hosted) | **Personal** |

**PersonalAI.SandboxMCP** is unique because it combines:
- **Provider independence** (any LLM + MCP support)
- **Personal infrastructure** (your Docker, your data)
- **Cost efficiency** (self-hosted, no per-execution fees)
- **Privacy** (code never leaves your hardware)

---

## Why This Matters

1. **Decouples execution from LLM** — Switch models without switching tools
2. **Ownership** — Your code runs on your infrastructure
3. **Cost** — No per-execution fees; amortized against your hardware
4. **Privacy** — Sensitive code never sent to hosted services
5. **Extensibility** — Other LLMs / MCP clients automatically supported

---

## Next Steps

1. **Dependency check**: Verify MCP SDK for .NET exists (or use raw stdio)
2. **Scaffold project**: Create `src/PersonalAI.SandboxMCP/`
3. **Implement phases 1–2**: Server skeleton + PersonalAiClient
4. **Add tests**: Phase 3 testing
5. **Document**: README with setup + usage examples
6. **Release**: Package as NuGet package or standalone executable

---

## Effort Estimate

| Phase | Effort | Notes |
|-------|--------|-------|
| 1. Skeleton | 2 hours | Project setup, DI, config |
| 2. MCP logic | 8 hours | Tool declaration, routing, client impl |
| 3. Integration | 4 hours | Deployment, Claude config, docs |
| 4. Testing | 4 hours | Unit + E2E tests |
| **Total** | **~18 hours** | One sprint, or 2–3 focused days |

---

## Future Enhancements

- **HTTP transport mode** — Run as a network service, not just stdio
- **Multi-session support** — Route to different PersonalAI sessions based on context
- **Timeout/rate limiting** — Prevent resource exhaustion
- **Approval flow** — Proxy approval prompts back to MCP client (if supported)
- **Logging** — Structured logs of all tool invocations
- **Metrics** — Track execution time, success rates, errors

---

## Timeline

**Tentative**:
- **Week 1**: Phases 1–2 (skeleton + client)
- **Week 2**: Phase 3 (integration + docs)
- **Week 3**: Phase 4 (testing) + public release (if open-sourcing)

Can be compressed to 1 week if focused.

---

## Decision: When to Build?

- **After** open-source prep (MCP_PLAN depends on PersonalAI being public)
- **Parallel** with docs/examples work
- **Before** v1.0 release (good differentiator)

This is a natural extension post-launch, not a blocker.
