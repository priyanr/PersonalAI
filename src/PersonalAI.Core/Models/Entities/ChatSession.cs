using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.Entities;

public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Title { get; set; }

    /// <summary>Which IAIProvider (Key) this session talks to — "ollama", "openai", "deepseek", etc.
    /// Can be changed mid-chat (PATCH settings); each LLM call resolves the provider and model afresh.</summary>
    public string ProviderKey { get; set; } = "ollama";
    public required string ModelName { get; set; }
    public string? SystemPrompt { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;

    /// <summary>When false, no tool definitions are advertised to Ollama for this session — the model can only reply in plain text.</summary>
    public bool ToolsEnabled { get; set; } = true;

    /// <summary>Sent as options.num_ctx on every /api/chat call for this session. Null uses Ollama's own default.</summary>
    public int? NumCtx { get; set; }

    /// <summary>When true, tool-result content is capped and old history is folded into a running
    /// summary (rather than sent raw) before being sent to Ollama, so long agentic turns don't
    /// silently overflow the context window. See COMPACTION_ARCHITECTURE.md.</summary>
    public bool TrimHistoryEnabled { get; set; } = true;

    /// <summary>Running summary of every ChatMessage up to and including SummarizedThroughSequenceNumber,
    /// folded in by ChatAgentService.EnsureHistoryCompactedAsync once enough history has aged out of the
    /// keep window. Null means nothing has been compacted yet. See COMPACTION_ARCHITECTURE.md.</summary>
    public string? HistorySummary { get; set; }

    /// <summary>Cursor: the highest ChatMessage.SequenceNumber already folded into HistorySummary. 0 means
    /// nothing has been compacted yet.</summary>
    public int SummarizedThroughSequenceNumber { get; set; }

    /// <summary>Caps how many LLM calls a single turn's agentic loop may make before it's stopped,
    /// overriding the global Agent:MaxIterations default. Null means "use the global default". The
    /// current/max call count is also relayed to the model itself each call (see ChatAgentService), so
    /// it can pace itself and wrap up before hitting the cap instead of being cut off mid-task.</summary>
    public int? MaxLlmCalls { get; set; }

    /// <summary>Lifetime count of LLM calls made across every turn of this session.</summary>
    public int TotalLlmCalls { get; set; }

    /// <summary>Lifetime prompt tokens across every provider call this session made (including history
    /// compaction), as reported by the provider. Calls whose provider reports no usage add nothing.</summary>
    public long TotalInputTokens { get; set; }

    /// <summary>Lifetime completion tokens, same basis as <see cref="TotalInputTokens"/>.</summary>
    public long TotalOutputTokens { get; set; }

    /// <summary>JSON array of Skill.Id values this session's model may invoke as tools (each exposed as
    /// "skill_&lt;slug&gt;", run via ISkillExecutionService and folded back in as one tool result). Only
    /// consulted when ToolsEnabled is true — Skills are advertised through the same tool channel.</summary>
    public string EnabledSkillIdsJson { get; set; } = "[]";

    /// <summary>Whether extended "thinking" models are allowed to emit reasoning tokens for this session.
    /// Null inherits AppSettings.ThinkingEnabled. Turning this off avoids a small reasoning model
    /// silently burning its whole output budget on hidden thinking with no visible answer.</summary>
    public bool? ThinkingEnabled { get; set; }

    /// <summary>Reasoning effort sent with every LLM call of this session's turns (one of
    /// ModelSettings.ReasoningEffortLevels). Null uses the model's ReasoningEffort setting on the Models page,
    /// else the model's own default. Not applied to history compaction.</summary>
    public string? ReasoningEffort { get; set; }

    // Per-tool toggles — only consulted when ToolsEnabled is true.
    public bool WebSearchEnabled { get; set; } = true;
    public bool ReadFileEnabled { get; set; } = true;
    public bool WriteFileEnabled { get; set; } = true;
    public bool EditFileEnabled { get; set; } = true;
    public bool ListFilesEnabled { get; set; } = true;
    public bool SearchFilesEnabled { get; set; } = true;

    /// <summary>Off by default (unlike the other tool toggles) — network scanning touches other
    /// devices on the user's LAN, not just this session's own sandbox, so it requires explicit
    /// per-session opt-in.</summary>
    public bool NetworkScanEnabled { get; set; } = false;

    public bool HttpRequestEnabled { get; set; } = true;

    /// <summary>Off by default, same posture as NetworkScanEnabled — this tool can drive mouse,
    /// keyboard, and screen capture across the entire host machine, not just this session's own
    /// sandbox, so it requires explicit per-session opt-in.</summary>
    public bool ComputerControlEnabled { get; set; } = false;

    /// <summary>Off by default, same posture as NetworkScanEnabled/ComputerControlEnabled — this tool
    /// gives the model a Docker container it can run arbitrary commands in, so it requires explicit
    /// per-session opt-in rather than being on by default like the in-process file tools.</summary>
    public bool SandboxEnabled { get; set; } = false;

    /// <summary>Off by default, same posture as SandboxEnabled — this tool can read and send email from
    /// the user's connected Gmail account, so it requires explicit per-session opt-in.</summary>
    public bool GmailEnabled { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ChatMessage> Messages { get; set; } = [];
    public List<ToolInvocation> ToolInvocations { get; set; } = [];
}
