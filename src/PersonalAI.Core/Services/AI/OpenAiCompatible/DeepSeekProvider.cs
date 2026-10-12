using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.AI.OpenAiCompatible;

public sealed class DeepSeekProvider(HttpClient httpClient, AIProviderConfigCache configCache, IOptions<AgentSettings> agentSettings)
    : OpenAiCompatibleProviderBase(httpClient, configCache, agentSettings)
{
    public const string ProviderKey = "deepseek";

    public override string Key => ProviderKey;
    public override string DisplayName => "DeepSeek";
    public override string DefaultBaseUrl => "https://api.deepseek.com";

    public override IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new ProviderConfigField("ApiKey", "API Key", ProviderConfigFieldType.Secret, Required: true),
        new ProviderConfigField("BaseUrl", "Base URL", ProviderConfigFieldType.Url, Required: false, DefaultValue: "https://api.deepseek.com"),
        AIProviderConfigCache.MaxOutputTokensConfigField
    ];

    // Only a session's explicit "off" is sent. On, or no preference (skill/agent runs, compaction), leaves
    // the model its own default — forcing thinking onto a non-reasoning model, or off for a reasoner a
    // skill was deliberately set up with, would each be a change nobody asked for.
    private protected override void ApplyThinking(OpenAiChatRequestWire requestBody, bool? think)
    {
        if (think == false)
            requestBody.Thinking = new OpenAiThinkingWire { Type = "disabled" };
    }

    private protected override bool SendsReasoningBack => true;

    protected override bool SupportsResponsesApi => false;

    public override bool SupportsThinkingToggle => true;

    public override IReadOnlyList<ProviderConfigField> ModelSettingFields => ModelSettings.OpenAiCompatibleWithThinkingFields;
}
