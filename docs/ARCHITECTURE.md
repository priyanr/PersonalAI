# Architecture

## Detailed System Design

```
┌───────────────────────────────────────────────────────────────────────────┐
│  Browser (Next.js)                                                        │
│  Chat/Agents top tabs, then per-area pages:                               │
│  Sessions · Models · Agents (+runs) · Settings (General/Providers/        │
│  Secrets/Skills/Tools)                                                    │
│              Redux Store + SSE streaming, bearer token in every request   │
└──────────────────────────────┬──────────────────────────────────────────┬─┘
                                │ HTTP + SSE (Authorization: Bearer ...)   │
┌───────────────────────────────▼───────────────────────────────────────────┘
│  ASP.NET Core API (PersonalAI.Api)                                       │
│  Controllers: Sessions · Models · AppSettings · ToolInvocations ·        │
│  AIProviders · Secrets · Skills · Agents · Tools · Auth · GmailOAuth ·   │
│  SandboxHost (WS)                                                        │
│  BackgroundServices: AgentSchedulerHostedService (polls due Agents),     │
│  SandboxReaperHostedService (retires idle/orphaned sandboxes)            │
│  ┌──────────────────────────────────────────────────────────────────┐    │
│  │ Core Domain (PersonalAI.Core)                                    │    │
│  │  ChatAgentService (per-session agentic loop)                     │    │
│  │  SkillExecutionService / AgentExecutionService (unattended loops)│    │
│  │  AIProviderRegistry → IAIProvider (Ollama / OpenAI / DeepSeek)   │    │
│  │  ToolRegistry → 11 built-in tools                                │    │
│  │  SecretsCache / ToolConfigCache / AIProviderConfigCache          │    │
│  │  Repositories (EF Core / SQLite) for every entity below          │    │
│  │  SearXngSearchProvider · OAuthClient (delegated login)            │    │
│  │  SandboxHostRegistry (ISandboxHostClient, owns the one WebSocket)│    │
│  └───┬──────────┬────────────┬───────────────────────┬───────┬──────┘    │
│      │          │            │                       │       │           │
│  ┌───▼───┐ ┌───▼──────┐ ┌────▼─────┐        ┌────────▼───┐   │           │
│  │SQLite │ │Ollama/   │ │SearXNG   │        │OAuth2 / OIDC│   │           │
│  │(EF)   │ │OpenAI/   │ │(HTTP)    │        │(optional)   │   │           │
│  │       │ │DeepSeek  │ │          │        │             │   │           │
│  └───────┘ └──────────┘ └──────────┘        └─────────────┘   │           │
└─────────────────────────────────────────────────────────────┼───────────┘
                                                  ONE outbound  │ WebSocket
                                                  + shared host │ key
                                                                │
                          Docker host ◄────────────────────────┘
                         ┌──────────────────────────────────────────┐
                         │ PersonalAI.SandboxHost (dials OUT, never  │
                         │ accepts inbound; systemd or compose)      │
                         │   ├─ DockerSandboxManager → Docker Engine │
                         │   │  API (create/exec/destroy/list only)  │
                         │   └─ SandboxFileService → plain file I/O  │
                         │  ┌──────────┐  ┌──────────┐               │
                         │  │chat-sbx-A│  │agent-run-B│  (containers)│
                         │  │ /work    │  │ /work     │              │
                         │  └────┬─────┘  └────┬──────┘              │
                         │  bind-mounted from host:                  │
                         │  /data/sandbox/<ownerId>                  │
                         └──────────────────────────────────────────┘
```

## Solution Structure

```
PersonalAI.slnx                     # Solution file
├── src/
│   ├── PersonalAI.Api/               # ASP.NET Core Web API host
│   │   ├── Controllers/              # Sessions, Models, AppSettings, ToolInvocations,
│   │   │                             # AIProviders, Secrets, Skills, Agents, Tools, Auth, GmailOAuth
│   │   ├── BackgroundServices/       # AgentSchedulerHostedService, ChatTurnShutdownService
│   │   ├── Streaming/                # SseEventWriter (SSE serialization)
│   │   ├── Program.cs                # DI composition root
│   │   ├── Dockerfile                # Multi-stage .NET 10 build
│   │   └── appsettings.json          # All configuration sections
│   │
│   └── PersonalAI.Core/              # Domain + infrastructure (no web deps)
│       ├── Infrastructure/            # EF Core DbContext, configurations, migrations
│       ├── Interfaces/                # Service contracts (IChatAgentService, IAIProvider,
│       │                              # ISkillExecutionService, IAgentExecutionService, etc.)
│       ├── Models/
│       │   ├── DTOs/                  # API-level request/response records
│       │   ├── Entities/              # EF entities (+ Enums/ for status/trigger/schedule enums)
│       │   └── Settings/              # Strongly-typed config POCOs
│       ├── Services/
│       │   ├── AI/                    # AIProviderRegistry, AIProviderConfigCache/Repository,
│       │   │                          # Ollama/ and OpenAiCompatible/ (OpenAI + DeepSeek) providers
│       │   ├── Agent/                 # ChatAgentService (per-session agentic loop), stream events
│       │   ├── Agents/                # AgentExecutionService, AgentsRepository, AgentRunsRepository
│       │   ├── Auth/                  # OAuthClient + OAuthEndpointResolver (standard OAuth2/OIDC login)
│       │   ├── Scheduling/            # CronExpression, ScheduleCalculator (dependency-free scheduler)
│       │   ├── Search/                # SearXngSearchProvider
│       │   ├── Secrets/               # SecretsCache, SecretsRepository
│       │   ├── Sessions/              # ChatSessionRepository
│       │   ├── Settings/              # AppSettingsRepository + AppSettingsCache
│       │   ├── Skills/                # SkillExecutionService, SkillsRepository, SecretSubstitution
│       │   └── Tools/                 # ToolConfigCache/Repository, Gmail/ (OAuth client, token store)
│       ├── Services/Sandbox/          # SandboxHostRegistry (ISandboxHostClient), SandboxReconciler,
│       │                              # SandboxRepository, BoundedOutputAccumulator
│       └── Tools/                     # ITool interface, ToolRegistry, 11 built-in tools
│
│   ├── PersonalAI.Sandbox.Protocol/   # Zero-package-ref shared wire protocol: the polymorphic
│   │                                  # SandboxFrame hierarchy, SandboxFramePump, IFrameTransport,
│   │                                  # WebSocketFrameTransport. Referenced by both Core and SandboxHost;
│   │                                  # deliberately has nothing else in it so SandboxHost can reference
│   │                                  # it without pulling in EF Core/FlaUI/System.Drawing.Common via Core.
│   │
│   └── PersonalAI.SandboxHost/        # The broker: a .NET Generic Host worker deployed to the Docker
│       │                              # host. Dials OUT to the API's sandbox WebSocket.
│       ├── BrokerConnection.cs         # reconnect-with-backoff, owns the ClientWebSocket + SandboxFramePump
│       ├── DockerSandboxManager.cs     # create/exec/destroy/list via Docker.DotNet.Enhanced
│       ├── SandboxFileService.cs       # every FileOp — plain System.IO, no Docker involved
│       ├── SandboxPathResolver.cs      # confines every model-supplied path to the owner's directory
│       ├── README.md                   # bind-mount path gotcha, deployment modes, config reference
│       ├── Dockerfile                  # mcr.microsoft.com/dotnet/runtime — no docker CLI needed
│       └── personalai-sandboxhost.service  # systemd unit for the bare-metal deployment mode
│
├── tests/
│   └── PersonalAI.Tests/             # xUnit + Moq + FluentAssertions
│
├── web/                                # Next.js 15 frontend (standalone)
│   ├── src/app/                        # App Router: sessions, models, agents, settings/*, auth/callback
│   ├── src/components/                 # chat, sessions, models, agents, providers, secrets, skills,
│   │                                   # tools, sandbox (SandboxFileBrowser), auth, layout (PrimaryTabs,
│   │                                   # SettingsTabs)
│   ├── src/hooks/                      # useModels, useProviders, useSecrets, useSkills, useAgents,
│   │                                   # useTools, useChatStream, useSandboxStatus, useSandboxFiles, etc.
│   ├── src/lib/                        # API client (auth-aware), SSE reader, auth.js, enum mirrors
│   ├── src/store/                      # Redux Toolkit slices (one per domain area)
│   └── Dockerfile                      # Multi-stage Node 22 build
│
├── config/searxng/                     # SearXNG configuration
├── docs/                               # Architecture, API, configuration and design docs
├── data/                                # Runtime volumes (gitignored)
│   ├── ollama/                          # Ollama model storage
│   ├── sqlite/                          # SQLite database files
│   └── workspace/                       # Agent's sandboxed file workspace
│
└── docker-compose.yml                   # 5-service stack
```

## Service Layers

### API Controllers

**Sessions, Models, AppSettings**: Core chat state and app-wide config.

**ToolInvocations**: Tool approval/denial flow — user-facing gate for dangerous operations (file escapes, `sudo` exec, ...).

**AIProviders, Secrets, Skills, Agents, Tools**: CRUD + optional run endpoints. Secrets and tool config never echo values back in read responses, only `HasValue` flags.

**Auth, GmailOAuth**: Delegated OAuth2/OIDC login and Gmail OAuth token management (see [Concepts: Delegated Authentication](CONCEPTS.md#delegated-authentication)).

**SandboxHost (WebSocket)**: Accepts inbound WebSocket connection from `PersonalAI.SandboxHost` broker (host-key authenticated, `[AllowAnonymous]`). One persistent connection per deployed broker; multiplexes all container I/O.

### Background Services

**AgentSchedulerHostedService**: Polls due agents every `Scheduler:PollIntervalSeconds` (default 30s), fires them when ready.

**SandboxReaperHostedService**: Retires idle/orphaned sandboxes (timeouts, missed heartbeats, failed exec).

**ChatTurnShutdownService**: Finalizes interrupted turns on app shutdown (see [Concepts: Agentic Loop](CONCEPTS.md#agentic-loop)).

### Core Services

**ChatAgentService**: Orchestrates the per-session agentic loop (iterative tool calls + model replies). See [Concepts: Agentic Loop](CONCEPTS.md#agentic-loop-chatagentservice).

**SkillExecutionService** / **AgentExecutionService**: Run Skills and Agents through their own short agentic loops; no user approval flow (tools outside the allowlist are rejected with an error result instead of pausing).

**AIProviderRegistry**: Resolves provider by key, caches configs, manages the abstraction.

**ToolRegistry**: Registers the 11 built-in tools and wraps tool execution with context/approval logic.

**SandboxHostRegistry**: Owns the one outbound WebSocket to `PersonalAI.SandboxHost`, multiplexes all sandbox I/O, tracks broker connection state.

**Repositories & Caches**: EF Core repositories backed by SQLite; in-memory caches for secrets, tool config, and AI provider config (reloaded on every write, no TTL).

---

See [Concepts](CONCEPTS.md) for deep dives on specific services and [Configuration](CONFIGURATION.md) for all config sections.
