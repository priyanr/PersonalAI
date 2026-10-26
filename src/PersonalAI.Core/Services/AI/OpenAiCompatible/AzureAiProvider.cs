using System.Net.Http.Headers;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.AI.OpenAiCompatible;

/// <summary>Azure OpenAI / Azure AI Foundry via the v1 OpenAI-compatible route
/// (<c>{endpoint}/openai/v1</c>). Auth uses the <c>api-key</c> header, and the "model" sent on each
/// request is the Azure <em>deployment name</em>.</summary>
public sealed class AzureAiProvider(HttpClient httpClient, AIProviderConfigCache configCache, IOptions<AgentSettings> agentSettings)
    : OpenAiCompatibleProviderBase(httpClient, configCache, agentSettings)
{
    public const string ProviderKey = "azure";
    private const string V1Suffix = "/openai/v1";

    public override string Key => ProviderKey;
    public override string DisplayName => "Azure AI";
    public override string DefaultBaseUrl => "";

    // Azure AI Foundry can host many different model families (OpenAI, Anthropic, DeepSeek, Llama...)
    // behind the same endpoint, distinguished only by deployment name. Only OpenAI's reasoning-family
    // models (gpt-5, o1/o3/o4) reject the legacy max_tokens parameter; everything else — including
    // Claude/Anthropic deployments — expects it, and rejects the unrecognized max_completion_tokens
    // field with a 400 BadRequestForDependentService from the underlying model service.
    private static readonly string[] ReasoningModelMarkers = ["gpt-5", "o1", "o3", "o4"];

    protected override bool UseMaxCompletionTokens(string model)
    {
        return ReasoningModelMarkers.Any(marker => model.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public override IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new ProviderConfigField("ApiKey", "API Key", ProviderConfigFieldType.Secret, Required: true),
        new ProviderConfigField("BaseUrl", "Endpoint (e.g. https://my-resource.openai.azure.com)", ProviderConfigFieldType.Url, Required: true),
        new ProviderConfigField("Deployments", "Deployment names (comma-separated)", ProviderConfigFieldType.Text, Required: false),
        AIProviderConfigCache.MaxOutputTokensConfigField
    ];

    // Accepts whatever the portal shows (resource root, /openai, /openai/v1, a full /chat/completions
    // URL, a Foundry project URL...) and reduces it to the resource origin + /openai/v1.
    protected override string NormalizeBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                "Azure AI endpoint is not configured. Set it (e.g. https://my-resource.openai.azure.com) under Settings → Providers → Azure AI.");
        return uri.GetLeftPart(UriPartial.Authority) + V1Suffix;
    }

    protected override void ApplyAuth(HttpRequestMessage request, string apiKey)
    {
        request.Headers.TryAddWithoutValidation("api-key", apiKey);
    }

    // Azure's /models lists base models, not the deployments you can actually call, so prefer the
    // user-declared deployment names and only fall back to /models when none are set.
    public override async Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        if (GetConfigValue("Deployments") is { Length: > 0 } list)
        {
            return [.. list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(d => new AiModelInfo(d, 0, null, null, null, null))];
        }
        return await base.ListModelsAsync(ct);
    }
}
