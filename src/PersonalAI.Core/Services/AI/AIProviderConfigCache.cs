using System.Collections.Concurrent;
using PersonalAI.Core.Models.DTOs;

namespace PersonalAI.Core.Services.AI;

/// <summary>In-memory mirror of AIProviderConfig (and the per-model ModelConfig settings), kept in sync with
/// the DB by the startup load and by AIProvidersController / ModelsController after every update, so providers
/// can read connection details and model capabilities synchronously on the hot path without a DB round-trip
/// per chat call.</summary>
public sealed class AIProviderConfigCache
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    // Keyed by "{providerKey}\n{modelName}" — model names may contain ':' and '/'.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string?>> _models =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string?> NoSettings = new Dictionary<string, string?>();

    public string? GetValue(string providerKey, string fieldName) =>
        _providers.TryGetValue(providerKey, out var fields) && fields.TryGetValue(fieldName, out var value)
            ? value
            : null;

    /// <summary>The provider's own "MaxOutputTokens" override when set to a positive integer, else
    /// <paramref name="fallback"/> (Agent:MaxOutputTokens). Lets a reasoning-heavy provider get a bigger
    /// per-call budget — its thinking counts against the same cap — without raising it for every provider.</summary>
    public int ResolveMaxOutputTokens(string providerKey, int fallback) =>
        int.TryParse(GetValue(providerKey, MaxOutputTokensField), out var value) && value > 0 ? value : fallback;

    /// <summary>The model's own MaxOutputTokens setting when set, else the provider-level resolution.</summary>
    public int ResolveMaxOutputTokens(string providerKey, string model, int fallback) =>
        int.TryParse(GetModelValue(providerKey, model, MaxOutputTokensField), out var value) && value > 0
            ? value
            : ResolveMaxOutputTokens(providerKey, fallback);

    public const string MaxOutputTokensField = "MaxOutputTokens";

    /// <summary>A sanity ceiling only — the real limit is per model, and a value above it is rejected by the
    /// provider itself (a 400 on the next chat call, shown in the chat).</summary>
    public const int MaxOutputTokensUpperBound = 1_000_000;

    public static ProviderConfigField MaxOutputTokensConfigField { get; } = new(
        MaxOutputTokensField, "Max output tokens per call (blank = Agent:MaxOutputTokens; includes reasoning tokens; must not exceed the model's own limit)",
        ProviderConfigFieldType.Number, Required: false);

    public void SetProvider(string providerKey, IReadOnlyDictionary<string, string?> values)
    {
        var fields = new ConcurrentDictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        _providers[providerKey] = fields;
    }

    private static string ModelKey(string providerKey, string model) => $"{providerKey}\n{model}";

    /// <summary>All of a model's saved settings (empty when none).</summary>
    public IReadOnlyDictionary<string, string?> GetModelSettings(string providerKey, string model) =>
        _models.TryGetValue(ModelKey(providerKey, model), out var settings) ? settings : NoSettings;

    /// <summary>One model setting, or null when unset, blank or "auto".</summary>
    public string? GetModelValue(string providerKey, string model, string fieldName) =>
        GetModelSettings(providerKey, model).TryGetValue(fieldName, out var value)
        && !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, ModelSettings.Auto, StringComparison.OrdinalIgnoreCase)
            ? value
            : null;

    /// <summary>Replaces a model's settings; an empty set removes it.</summary>
    public void SetModel(string providerKey, string model, IReadOnlyDictionary<string, string?> values)
    {
        var kept = values
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        if (kept.Count == 0)
            _models.TryRemove(ModelKey(providerKey, model), out _);
        else
            _models[ModelKey(providerKey, model)] = kept;
    }

    /// <summary>True when the user switched tool calling off for this model.</summary>
    public bool ToolsDisabled(string providerKey, string model) =>
        string.Equals(GetModelValue(providerKey, model, ModelSettings.ToolCallingField), ModelSettings.Disabled, StringComparison.OrdinalIgnoreCase);

    /// <summary>The model's Thinking override when pinned on/off, else the caller's <paramref name="think"/>.</summary>
    public bool? ResolveThink(string providerKey, string model, bool? think) =>
        GetModelValue(providerKey, model, ModelSettings.ThinkingField)?.ToLowerInvariant() switch
        {
            ModelSettings.AlwaysOn => true,
            ModelSettings.AlwaysOff => false,
            _ => think
        };
}
