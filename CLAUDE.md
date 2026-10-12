# PersonalAI

> A self-hosted personal agent platform: pluggable AI providers (Ollama/OpenAI/DeepSeek), tool-use with workspace sandboxing plus full Docker sandboxes for arbitrary code execution, a secrets vault, prompt-driven Skills, scheduled Agents that orchestrate those skills, and delegated OAuth2 login — all behind one ASP.NET Core API, one Next.js frontend, and one broker process on the Docker host.

## Quick Start

```bash
# Docker (full stack)
cp .env.example .env
docker compose up -d

# Local dev — backend
dotnet run --project src/PersonalAI.Api

# Local dev — frontend
cd web && npm install && npm run dev
```

- **API**: http://localhost:5080 (or `API_PORT` in `.env`)
- **Web UI**: http://localhost:3000 (or `WEB_PORT` in `.env`)
- **Ollama**: http://localhost:11434 (managed by docker-compose or run separately)
- **SearXNG**: http://localhost:8888 (managed by docker-compose)

Auth is off by default (`AUTH_ENABLED=false` in `.env.example` / docker-compose).

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend | .NET 10, ASP.NET Core Web API, C# 13 |
| ORM / DB | Entity Framework Core 10, SQLite |
| AI Providers | Pluggable `IAIProvider`: Ollama, OpenAI, DeepSeek |
| Web Search | SearXNG (self-hosted meta-search engine) |
| Frontend | Next.js 16, React 19, Redux Toolkit |
| Sandbox broker | .NET Generic Host worker, Docker API |
| Deployment | Docker Compose (5 services) |

## Architecture Overview

```
┌─────────────────────────┐
│  Browser (Next.js)      │
│  Chat · Agents · Setup  │
└────────────┬────────────┘
             │ HTTP + SSE
┌────────────▼────────────────────────┐
│  ASP.NET Core API (PersonalAI.Api)   │
│  Controllers · Services · DI         │
│  ├─ ChatAgentService (agentic loop) │
│  ├─ SkillExecutionService           │
│  ├─ AgentExecutionService           │
│  ├─ AIProviderRegistry              │
│  ├─ ToolRegistry (11 tools)         │
│  └─ SandboxHostRegistry (WebSocket) │
└────────────┬────────────────────────┘
             │ SQLite + Outbound WS
      ┌──────▼──────────────────┐
      │ Docker host + volumes   │
      │ PersonalAI.SandboxHost  │
      │ (broker, dial-out)      │
      └─────────────────────────┘
```

## Documentation

### Essential
- **[Architecture](docs/ARCHITECTURE.md)** — Detailed system design, solution structure, service layers
- **[Core Concepts](docs/CONCEPTS.md)** — ChatAgentService (agentic loop), Skills, Agents, Secrets, Tools, Sandbox
- **[Configuration](docs/CONFIGURATION.md)** — All config sections, environment variables, settings
- **[API Endpoints](docs/API.md)** — Complete endpoint reference (Chat, Agents, Skills, Sandbox, Auth, etc.)

### Deployment & Operations
- **[Deployment](docs/DEPLOYMENT.md)** — Docker Compose, dotnet run, publishing, remote sandbox host
- **[Sandbox Architecture](docs/SANDBOX_ARCHITECTURE.md)** — Deep dive on Docker sandbox broker, security, lifecycle
- **[Compaction Architecture](docs/COMPACTION_ARCHITECTURE.md)** — History trimming design for long conversations

### Development
- **[Frontend Architecture](docs/FRONTEND.md)** — Next.js pages, Redux state, SSE client *(read only if developing the UI)*
- **[Testing](docs/TESTING.md)** — Test suites, coverage, running tests *(read only if adding/maintaining tests)*


## Database Schema

See [Configuration](docs/CONFIGURATION.md#database-schema) for the complete entity list and ERD.

## Running Tests

```bash
dotnet test PersonalAI.slnx
```

638 tests across Agent, Controllers, Infrastructure, Services, Tools, and Sandbox subsystems.

## Key Design Decisions

1. **Pluggable AI providers** — Ollama, OpenAI, DeepSeek all behind `IAIProvider` abstraction
2. **Agentic loop as a reusable service** — Chat, Skills, and Agents all use the same loop with different I/O
3. **Outbound-only sandbox broker** — SandboxHost dials the API, never accepts inbound; lives on the Docker host
4. **Bearer token auth (no cookies)** — Delegated OAuth2/OIDC, tokens in localStorage, standard HTTP headers
5. **Per-owner sandboxes** — One Docker container per chat session or agent run, all under one owner's directory
6. **Stateless HTTP + in-memory turn runner** — Turns run on the server; SSE clients subscribe; reload re-attaches

---

**Questions?** Check the linked docs or ask in an issue.
