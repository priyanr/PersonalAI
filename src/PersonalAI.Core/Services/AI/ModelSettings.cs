using PersonalAI.Core.Models.DTOs;

namespace PersonalAI.Core.Services.AI;

/// <summary>Per-model capability settings a user can pin on the Models page. Every field is optional:
/// blank (or <see cref="Auto"/>) keeps the provider's own detection/defaults. Providers declare which of
/// these apply to them via <see cref="Interfaces.IAIProvider.ModelSettingFields"/>.</summary>
public static class ModelSettings
{
    public const string Auto = "auto";

    public const string OutputLimitParameterField = "OutputLimitParameter";
    public const string MaxTokensParameter = "max_tokens";
    public const string MaxCompletionTokensParameter = "max_completion_tokens";
    public const string OmitParameter = "none";

    public const string MaxOutputTokensField = AIProviderConfigCache.MaxOutputTokensField;

    public const string ToolCallingField = "ToolCalling";
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";

    public const string ReasoningEffortField = "ReasoningEffort";

    /// <summary>The reasoning_effort values OpenAI-wire models accept, lowest first. Also what a chat session or
    /// agent may choose as its own effort level (see <see cref="NormalizeReasoningEffort"/>).</summary>
    public static IReadOnlyList<string> ReasoningEffortLevels { get; } = ["none", "minimal", "low", "medium", "high", "xhigh"];

    public const string ApiField = "Api";
    public const string ChatCompletionsApi = "chat_completions";
    public const string ResponsesApi = "responses";

    public const string ThinkingField = "Thinking";
    public const string AlwaysOn = "on";
    public const string AlwaysOff = "off";

    public static ProviderConfigField OutputLimitParameter { get; } = new(
        OutputLimitParameterField, "Output limit parameter", ProviderConfigFieldType.Select, Required: false,
        DefaultValue: Auto, Options: [Auto, MaxTokensParameter, MaxCompletionTokensParameter, OmitParameter],
        Help: "Which request field carries the output cap. Newer OpenAI reasoning models reject max_tokens " +
              "(\"Use 'max_completion_tokens' instead\"); many other models reject max_completion_tokens. " +
              "Auto guesses from the model name and switches automatically after such an error. " +
              "\"none\" sends neither.");

    public static ProviderConfigField MaxOutputTokens { get; } = new(
        MaxOutputTokensField, "Max output tokens per call", ProviderConfigFieldType.Number, Required: false,
        Help: "Blank = the provider's setting, then Agent:MaxOutputTokens. Includes reasoning tokens; must not exceed the model's own limit.");

    public static ProviderConfigField ToolCalling { get; } = new(
        ToolCallingField, "Tool calling", ProviderConfigFieldType.Select, Required: false,
        DefaultValue: Auto, Options: [Auto, Enabled, Disabled],
        Help: "Disable for models that don't support function tools — chats, skills and agents then run without tools.");

    public static ProviderConfigField ReasoningEffort { get; } = new(
        ReasoningEffortField, "Reasoning effort", ProviderConfigFieldType.Select, Required: false,
        DefaultValue: Auto, Options: [Auto, .. ReasoningEffortLevels],
        Help: "Sent as reasoning_effort (reasoning.effort on /responses). Auto sends nothing (the model's default). " +
              "A chat's or agent's own reasoning effort takes precedence. Only reasoning models accept it.");

    public static ProviderConfigField Api { get; } = new(
        ApiField, "API", ProviderConfigFieldType.Select, Required: false,
        DefaultValue: Auto, Options: [Auto, ChatCompletionsApi, ResponsesApi],
        Help: "Which OpenAI endpoint to call. Some reasoning models only accept function tools on /responses " +
              "(\"use /v1/responses or set reasoning_effort to 'none'\"). Auto uses /chat/completions and switches " +
              "to /responses after such an error.");

    public static ProviderConfigField Thinking { get; } = new(
        ThinkingField, "Thinking", ProviderConfigFieldType.Select, Required: false,
        DefaultValue: Auto, Options: [Auto, AlwaysOn, AlwaysOff],
        Help: "Auto follows the chat's thinking toggle. On/Off overrides it for this model everywhere.");

    /// <summary>Settings for OpenAI-wire providers that have no thinking switch (OpenAI, Azure — both also
    /// serve the Responses API).</summary>
    public static IReadOnlyList<ProviderConfigField> OpenAiCompatibleFields { get; } =
        [OutputLimitParameter, MaxOutputTokens, ToolCalling, ReasoningEffort, Api];

    /// <summary>Settings for OpenAI-wire providers that also have a thinking switch (DeepSeek).</summary>
    public static IReadOnlyList<ProviderConfigField> OpenAiCompatibleWithThinkingFields { get; } =
        [OutputLimitParameter, MaxOutputTokens, ToolCalling, ReasoningEffort, Thinking];

    public static IReadOnlyList<ProviderConfigField> OllamaFields { get; } =
        [MaxOutputTokens, ToolCalling, Thinking];

    /// <summary>Canonicalizes a chat session's or agent's reasoning effort: blank or "auto" becomes null (the
    /// model's setting, else its own default). Returns false for anything not in <see cref="ReasoningEffortLevels"/>.</summary>
    public static bool TryNormalizeReasoningEffort(string? value, out string? normalized)
    {
        normalized = null;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, Auto, StringComparison.OrdinalIgnoreCase))
            return true;
        normalized = ReasoningEffortLevels.FirstOrDefault(l => string.Equals(l, trimmed, StringComparison.OrdinalIgnoreCase));
        return normalized is not null;
    }

    public static string InvalidReasoningEffortMessage(string? value)
    {
        return $"'{value}' is not a valid reasoning effort. Allowed: {string.Join(", ", ReasoningEffortLevels)} (or blank for the model's default).";
    }

    /// <summary>Checks <paramref name="values"/> against <paramref name="fields"/> and returns them
    /// with canonical field names and option casing, minus blank/"auto" entries (those mean "use the default").
    /// On an invalid entry returns null and sets <paramref name="error"/>.</summary>
    public static Dictionary<string, string?>? Normalize(
        IReadOnlyList<ProviderConfigField> fields, IReadOnlyDictionary<string, string?> values, out string? error)
    {
        error = null;
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, raw) in values)
        {
            var field = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                error = $"Unknown model setting '{name}'.";
                return null;
            }

            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value) || string.Equals(value, Auto, StringComparison.OrdinalIgnoreCase))
                continue;

            switch (field.Type)
            {
                case ProviderConfigFieldType.Select:
                    var option = field.Options!.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
                    if (option is null)
                    {
                        error = $"'{value}' is not a valid value for {field.Label}. Allowed: {string.Join(", ", field.Options!)}.";
                        return null;
                    }
                    value = option;
                    break;
                case ProviderConfigFieldType.Number:
                    if (!int.TryParse(value, out var number) || number < 1 || number > AIProviderConfigCache.MaxOutputTokensUpperBound)
                    {
                        error = $"{field.Label} must be a whole number between 1 and {AIProviderConfigCache.MaxOutputTokensUpperBound:N0} (or blank for the default).";
                        return null;
                    }
                    value = number.ToString();
                    break;
            }
            normalized[field.Name] = value;
        }
        return normalized;
    }
}
