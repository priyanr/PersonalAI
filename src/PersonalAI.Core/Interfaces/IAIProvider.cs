using PersonalAI.Core.Models.DTOs;

namespace PersonalAI.Core.Interfaces;

/// <summary>A pluggable AI backend (Ollama, OpenAI, DeepSeek, ...). Implementations read their own
/// connection details (base URL, API key, ...) from AIProviderConfigCache at call time, so config
/// changes made via the /api/ai-providers UI take effect without an app restart.</summary>
public interface IAIProvider
{
    /// <summary>Stable, lowercase identifier — used as the DB config key and the providerKey route/query param.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>Static metadata describing what configuration this provider needs. Actual values live
    /// in AIProviderConfig, keyed by (Key, field.Name).</summary>
    IReadOnlyList<ProviderConfigField> ConfigFields { get; }

    /// <param name="think">True/false when the caller has an explicit thinking preference (a chat session's
    /// ThinkingEnabled); null when it doesn't (skill/agent runs, history compaction), leaving the provider's
    /// own default in place.</param>
    /// <param name="reasoningEffort">The caller's reasoning effort (one of ModelSettings.ReasoningEffortLevels) — a
    /// chat session's or agent's own choice; null leaves the model's ReasoningEffort setting, else its default.</param>
    IAsyncEnumerable<AiChatChunk> ChatStreamAsync(
        string model,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        int? numCtx,
        bool? think,
        string? reasoningEffort,
        CancellationToken ct);

    /// <summary>Whether <c>think</c> actually changes anything for this provider — decides whether a
    /// "turn thinking off" hint is worth giving the user.</summary>
    bool SupportsThinkingToggle => false;

    /// <summary>The per-model settings (see ModelSettings) this provider honors — rendered by the Models
    /// page's "Configure" dialog. Values live in ModelConfig, keyed by (Key, model, field.Name).</summary>
    IReadOnlyList<ProviderConfigField> ModelSettingFields => [];

    Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct);

    /// <summary>Streams progress for pulling a model into local storage. Providers with no such concept
    /// (hosted APIs like OpenAI/DeepSeek) throw NotSupportedException.</summary>
    IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct);
}
