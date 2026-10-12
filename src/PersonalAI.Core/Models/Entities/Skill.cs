namespace PersonalAI.Core.Models.Entities;

/// <summary>A prompt-driven capability: a system prompt plus a restricted set of tools it may call and
/// secrets it may read. Executed on demand (or, from Stage 4, by a scheduled Agent) via SkillExecutionService,
/// which runs its own short agentic loop independent of any ChatSession.</summary>
public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The skill's system prompt — the instructions executed on every run.</summary>
    public required string Prompt { get; set; }

    /// <summary>Which IAIProvider (Key) this skill talks to — "ollama", "openai", "deepseek", etc.</summary>
    public string ProviderKey { get; set; } = "ollama";
    public required string ModelName { get; set; }

    /// <summary>Sent as options.num_ctx on every run. Null uses the provider's own default.</summary>
    public int? NumCtx { get; set; }

    /// <summary>JSON array of ITool.Name values this skill is allowed to call — an allowlist, unlike
    /// ChatSession's opt-out toggles, since a skill runs unattended and should only reach the tools it
    /// actually needs.</summary>
    public string EnabledToolsJson { get; set; } = "[]";

    /// <summary>JSON array of Secret.Key values this skill may read. At run time, resolved values are
    /// never shown to the model — only substituted into tool call arguments in place of a
    /// "{{secret.KEY}}" placeholder immediately before the tool executes.</summary>
    public string SecretKeysJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
