using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;

namespace PersonalAI.Tests.Services;

public class AIProviderConfigServiceTests
{
    private sealed class StubProvider(string key, IReadOnlyList<ProviderConfigField> fields) : IAIProvider
    {
        public string Key => key;
        public string DisplayName => key;
        public IReadOnlyList<ProviderConfigField> ConfigFields => fields;
        public IReadOnlyList<ProviderConfigField> ModelSettingFields => ModelSettings.OpenAiCompatibleFields;
        public IAsyncEnumerable<AiChatChunk> ChatStreamAsync(string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools, int? numCtx, bool? think, string? reasoningEffort, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AiModelInfo>>([new AiModelInfo("gpt-6-astra", 0, null, null, null, null), new AiModelInfo("other", 0, null, null, null, null)]);
        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class UnreachableProvider : IAIProvider
    {
        public string Key => "ollama";
        public string DisplayName => "Ollama";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public IAsyncEnumerable<AiChatChunk> ChatStreamAsync(string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools, int? numCtx, bool? think, string? reasoningEffort, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) => throw new HttpRequestException("connection refused");
        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly IReadOnlyList<ProviderConfigField> OpenAiFields =
    [
        new ProviderConfigField("ApiKey", "API Key", ProviderConfigFieldType.Secret, Required: true),
        new ProviderConfigField("BaseUrl", "Base URL", ProviderConfigFieldType.Url, Required: false, DefaultValue: "https://api.openai.com/v1"),
        AIProviderConfigCache.MaxOutputTokensConfigField
    ];

    private static (AIProviderConfigService Service, AppDbContext Db, AIProviderConfigCache Cache) CreateService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var services = new ServiceCollection();
        services.AddHybridCache();
        var hybridCache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        var cache = new AIProviderConfigCache();
        var registry = new AIProviderRegistry([new StubProvider("openai", OpenAiFields), new StubProvider("azure", []), new UnreachableProvider()]);
        return (new AIProviderConfigService(db, new AIProviderConfigRepository(db), registry, cache, hybridCache, NullLogger<AIProviderConfigService>.Instance), db, cache);
    }

    private static UpdateAIProviderConfigRequest Values(params (string Name, string? Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value));

    // ---- provider settings ----

    [Fact]
    public async Task GetProviders_NoConfigYet_ReportsNotConfiguredAndNoSecretValue()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var openai = (await service.GetProviders(CancellationToken.None)).Single(p => p.Key == "openai");

        openai.IsConfigured.Should().BeFalse();
        openai.Fields.Single(f => f.Name == "ApiKey").HasValue.Should().BeFalse();
        openai.Fields.Single(f => f.Name == "ApiKey").Value.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProvider_SetsRequiredSecretField_BecomesConfigured()
    {
        var (service, db, cache) = CreateService();
        await using var _1 = db;

        var dto = await service.UpdateProvider("openai", Values(("ApiKey", "sk-abc")), CancellationToken.None);

        dto.IsConfigured.Should().BeTrue();
        dto.Fields.Single(f => f.Name == "ApiKey").HasValue.Should().BeTrue();
        dto.Fields.Single(f => f.Name == "ApiKey").Value.Should().BeNull("secret values are never echoed back");
        cache.GetValue("openai", "ApiKey").Should().Be("sk-abc");
        (await service.GetProviders(CancellationToken.None)).Single(p => p.Key == "openai").IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateProvider_BlankSecretValue_LeavesPreviouslySetKeyUntouched()
    {
        var (service, db, cache) = CreateService();
        await using var _1 = db;

        await service.UpdateProvider("openai", Values(("ApiKey", "sk-abc")), CancellationToken.None);
        var dto = await service.UpdateProvider("openai", Values(("ApiKey", ""), ("BaseUrl", "https://custom")), CancellationToken.None);

        dto.Fields.Single(f => f.Name == "ApiKey").HasValue.Should().BeTrue("a blank secret in the request must not clear the stored key");
        dto.Fields.Single(f => f.Name == "BaseUrl").Value.Should().Be("https://custom");
        cache.GetValue("openai", "ApiKey").Should().Be("sk-abc");
    }

    [Fact]
    public async Task UpdateProvider_Twice_UpdatesTheRowAndLeavesOtherFieldsAlone()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        await service.UpdateProvider("openai", Values(("ApiKey", "sk-1"), ("BaseUrl", "https://a")), CancellationToken.None);
        await service.UpdateProvider("openai", Values(("ApiKey", "sk-2")), CancellationToken.None);

        db.AIProviderConfigs.Select(c => new { c.FieldName, c.Value }).Should().BeEquivalentTo(
            [new { FieldName = "ApiKey", Value = (string?)"sk-2" }, new { FieldName = "BaseUrl", Value = (string?)"https://a" }]);
    }

    [Fact]
    public async Task UpdateProvider_FieldNameInOtherCase_UpdatesTheExistingRow()
    {
        var (service, db, cache) = CreateService();
        await using var _1 = db;

        await service.UpdateProvider("openai", Values(("ApiKey", "sk-1")), CancellationToken.None);
        await service.UpdateProvider("openai", Values(("apikey", "sk-2")), CancellationToken.None);

        db.AIProviderConfigs.Select(c => new { c.FieldName, c.Value }).Should().BeEquivalentTo(
            [new { FieldName = "ApiKey", Value = (string?)"sk-2" }], "a second row differing only in case breaks the startup cache load");
        cache.GetValue("openai", "ApiKey").Should().Be("sk-2");
    }

    [Fact]
    public async Task UpdateProvider_MissingRequiredField_IsInvalidAndNamesIt()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var act = () => service.UpdateProvider("openai", Values(("BaseUrl", "https://custom")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>().WithMessage("*ApiKey*");
        db.AIProviderConfigs.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateProvider_UnknownField_IsInvalid()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var act = () => service.UpdateProvider("openai", Values(("NotAField", "x")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task UpdateProvider_UnknownProvider_IsNotFound()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var act = () => service.UpdateProvider("does-not-exist", Values(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("16k")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("2000000")]
    public async Task UpdateProvider_InvalidMaxOutputTokens_IsInvalid(string value)
    {
        var (service, db, cache) = CreateService();
        await using var _1 = db;

        var act = () => service.UpdateProvider("openai", Values(("ApiKey", "sk"), ("MaxOutputTokens", value)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
        cache.GetValue("openai", "MaxOutputTokens").Should().BeNull();
    }

    [Theory]
    [InlineData("32000")]
    [InlineData("")]
    public async Task UpdateProvider_ValidOrBlankMaxOutputTokens_IsAccepted(string value)
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var dto = await service.UpdateProvider("openai", Values(("ApiKey", "sk"), ("MaxOutputTokens", value)), CancellationToken.None);

        dto.IsConfigured.Should().BeTrue();
    }

    // ---- models and per-model settings ----

    [Fact]
    public async Task UpdateModelSettings_SavesNormalizedValues_DropsAuto_AndUpdatesCache()
    {
        var (service, db, cache) = CreateService();
        await using var _ = db;

        var saved = await service.UpdateModelSettings("azure", "gpt-6-astra", new UpdateModelSettingsRequest(new()
        {
            ["outputlimitparameter"] = "MAX_COMPLETION_TOKENS",
            ["ToolCalling"] = "auto",
            ["MaxOutputTokens"] = " 16000 "
        }), CancellationToken.None);

        saved.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["OutputLimitParameter"] = "max_completion_tokens",
            ["MaxOutputTokens"] = "16000"
        });
        cache.GetModelValue("azure", "gpt-6-astra", ModelSettings.OutputLimitParameterField).Should().Be("max_completion_tokens");
        db.ModelConfigs.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("OutputLimitParameter", "max_output")]
    [InlineData("MaxOutputTokens", "lots")]
    [InlineData("MaxOutputTokens", "0")]
    [InlineData("Temperature", "1")]
    public async Task UpdateModelSettings_InvalidValue_IsRejected(string field, string value)
    {
        var (service, db, cache) = CreateService();
        await using var _ = db;

        var act = () => service.UpdateModelSettings("azure", "gpt-6-astra", new UpdateModelSettingsRequest(new() { [field] = value }), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
        cache.GetModelSettings("azure", "gpt-6-astra").Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateModelSettings_UnknownProvider_IsNotFound()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var act = () => service.UpdateModelSettings("nope", "m", new UpdateModelSettingsRequest([]), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetModels_MergesSavedSettingsIntoEachModel()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;
        await service.UpdateModelSettings("azure", "gpt-6-astra", new UpdateModelSettingsRequest(new() { ["ToolCalling"] = "disabled" }), CancellationToken.None);

        var models = await service.GetModels("azure", CancellationToken.None);

        models.Single(m => m.Name == "gpt-6-astra").Settings.Should().ContainKey("ToolCalling").WhoseValue.Should().Be("disabled");
        models.Single(m => m.Name == "other").Settings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetModels_UnreachableProvider_IsUpstreamUnavailable()
    {
        var (service, db, _) = CreateService();
        await using var _1 = db;

        var act = () => service.GetModels("ollama", CancellationToken.None);

        await act.Should().ThrowAsync<UpstreamUnavailableException>().WithMessage("Could not reach Ollama: connection refused");
    }

    [Fact]
    public async Task ResetModelSettings_ClearsDbAndCache()
    {
        var (service, db, cache) = CreateService();
        await using var _ = db;
        await service.UpdateModelSettings("azure", "gpt-6-astra", new UpdateModelSettingsRequest(new() { ["ReasoningEffort"] = "low" }), CancellationToken.None);

        await service.ResetModelSettings("azure", "gpt-6-astra", CancellationToken.None);

        db.ModelConfigs.Should().BeEmpty();
        cache.GetModelSettings("azure", "gpt-6-astra").Should().BeEmpty();
    }

    [Fact]
    public void GetModelSettingFields_ReturnsProviderFieldsWithOptions()
    {
        var (service, db, _) = CreateService();
        using var _1 = db;

        var fields = service.GetModelSettingFields("azure");

        fields.Select(f => f.Name).Should().Equal("OutputLimitParameter", "MaxOutputTokens", "ToolCalling", "ReasoningEffort", "Api");
        fields.Single(f => f.Name == "OutputLimitParameter").Options.Should().Contain("max_completion_tokens");
    }
}
