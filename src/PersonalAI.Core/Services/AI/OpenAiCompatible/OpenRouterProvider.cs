using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.AI.OpenAiCompatible;

/// <summary>OpenRouter: one OpenAI-compatible endpoint fronting many model vendors. Model ids are
/// "vendor/model" (e.g. <c>anthropic/claude-sonnet-4.5</c>).</summary>
public sealed class OpenRouterProvider(HttpClient httpClient, AIProviderConfigCache configCache, IOptions<AgentSettings> agentSettings)
    : OpenAiCompatibleProviderBase(httpClient, configCache, agentSettings)
{
    public const string ProviderKey = "openrouter";

    public override string Key => ProviderKey;
    public override string DisplayName => "OpenRouter";
    public override string DefaultBaseUrl => "https://openrouter.ai/api/v1";

    public override IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new ProviderConfigField("ApiKey", "API Key", ProviderConfigFieldType.Secret, Required: true),
        new ProviderConfigField("BaseUrl", "Base URL", ProviderConfigFieldType.Url, Required: false, DefaultValue: "https://openrouter.ai/api/v1"),
        AIProviderConfigCache.MaxOutputTokensConfigField
    ];

    // OpenRouter normalizes every upstream model to the classic /chat/completions shape.
    protected override bool SupportsResponsesApi => false;
}
