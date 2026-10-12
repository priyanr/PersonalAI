namespace PersonalAI.Core.Models.Entities;

/// <summary>Single-row global configuration, not tied to any chat session.</summary>
public class AppSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>Extra guidance appended to the web_search tool's description sent to the model.
    /// Null uses the built-in default (see AppSettingsCache.DefaultWebSearchGuidance).</summary>
    public string? WebSearchGuidance { get; set; }

    /// <summary>Global default for whether extended "thinking" models are allowed to emit reasoning
    /// tokens. A session with ThinkingEnabled = null falls back to this. See ChatSession.ThinkingEnabled.</summary>
    public bool ThinkingEnabled { get; set; } = true;
}
