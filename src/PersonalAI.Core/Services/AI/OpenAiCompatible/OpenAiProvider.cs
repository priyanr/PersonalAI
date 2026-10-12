using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.AI.OpenAiCompatible;

public sealed class OpenAiProvider(HttpClient httpClient, AIProviderConfigCache configCache, IOptions<AgentSettings> agentSettings)
    : OpenAiCompatibleProviderBase(httpClient, configCache, agentSettings)
{
    public const string ProviderKey = "openai";

    public override string Key => ProviderKey;
    public override string DisplayName => "OpenAI";
    public override string DefaultBaseUrl => "https://api.openai.com/v1";

    // Newer OpenAI models (gpt-5, o-series) reject the legacy max_tokens parameter. A custom BaseUrl
    // (LM Studio, vLLM, LiteLLM...) may only understand max_tokens, so only switch for real OpenAI.
    protected override bool UseMaxCompletionTokens(string model) =>
        GetConfigValue("BaseUrl") is not { Length: > 0 } url
        || url.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

    public override IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new ProviderConfigField("ApiKey", "API Key", ProviderConfigFieldType.Secret, Required: true),
        new ProviderConfigField("BaseUrl", "Base URL", ProviderConfigFieldType.Url, Required: false, DefaultValue: "https://api.openai.com/v1"),
        AIProviderConfigCache.MaxOutputTokensConfigField
    ];
}
