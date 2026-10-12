using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;

namespace PersonalAI.Tests.Controllers;

public class ModelsControllerTests
{
    private sealed class StubProvider : IAIProvider
    {
        public string Key => "azure";
        public string DisplayName => "Azure AI";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public IReadOnlyList<ProviderConfigField> ModelSettingFields => ModelSettings.OpenAiCompatibleFields;
        public IAsyncEnumerable<AiChatChunk> ChatStreamAsync(string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools, int? numCtx, bool? think, string? reasoningEffort, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AiModelInfo>>([new AiModelInfo("gpt-6-astra", 0, null, null, null, null), new AiModelInfo("other", 0, null, null, null, null)]);
        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (ModelsController Controller, AppDbContext Db, AIProviderConfigCache Cache) CreateController()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var services = new ServiceCollection();
        services.AddHybridCache();
        var hybridCache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        var cache = new AIProviderConfigCache();
        var controller = new ModelsController(
            new AIProviderRegistry([new StubProvider()]), hybridCache, new ModelConfigRepository(db), cache, NullLogger<ModelsController>.Instance);
        return (controller, db, cache);
    }

    [Fact]
    public async Task UpdateSettings_SavesNormalizedValues_DropsAuto_AndUpdatesCache()
    {
        var (controller, db, cache) = CreateController();
        await using var _ = db;

        var result = await controller.UpdateSettings("azure", "gpt-6-astra", new UpdateModelSettingsRequest(new()
        {
            ["outputlimitparameter"] = "MAX_COMPLETION_TOKENS",
            ["ToolCalling"] = "auto",
            ["MaxOutputTokens"] = " 16000 "
        }), CancellationToken.None);

        var saved = ((OkObjectResult)result.Result!).Value as IReadOnlyDictionary<string, string?>;
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
    public async Task UpdateSettings_InvalidValue_IsRejected(string field, string value)
    {
        var (controller, db, cache) = CreateController();
        await using var _ = db;

        var result = await controller.UpdateSettings("azure", "gpt-6-astra",
            new UpdateModelSettingsRequest(new() { [field] = value }), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        cache.GetModelSettings("azure", "gpt-6-astra").Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateSettings_UnknownProvider_IsNotFound()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.UpdateSettings("nope", "m", new UpdateModelSettingsRequest([]), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetModels_MergesSavedSettingsIntoEachModel()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;
        await controller.UpdateSettings("azure", "gpt-6-astra",
            new UpdateModelSettingsRequest(new() { ["ToolCalling"] = "disabled" }), CancellationToken.None);

        var result = await controller.GetModels("azure", CancellationToken.None);
        var models = ((OkObjectResult)result.Result!).Value as IReadOnlyList<AiModelDto>;

        models!.Single(m => m.Name == "gpt-6-astra").Settings.Should().ContainKey("ToolCalling").WhoseValue.Should().Be("disabled");
        models!.Single(m => m.Name == "other").Settings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetSettings_ClearsDbAndCache()
    {
        var (controller, db, cache) = CreateController();
        await using var _ = db;
        await controller.UpdateSettings("azure", "gpt-6-astra",
            new UpdateModelSettingsRequest(new() { ["ReasoningEffort"] = "low" }), CancellationToken.None);

        var result = await controller.ResetSettings("azure", "gpt-6-astra", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ModelConfigs.Should().BeEmpty();
        cache.GetModelSettings("azure", "gpt-6-astra").Should().BeEmpty();
    }

    [Fact]
    public void GetSettingsFields_ReturnsProviderFieldsWithOptions()
    {
        var (controller, db, _) = CreateController();
        using var _1 = db;

        var result = controller.GetSettingsFields("azure");
        var fields = ((OkObjectResult)result.Result!).Value as IReadOnlyList<ModelSettingFieldDto>;

        fields!.Select(f => f.Name).Should().Equal("OutputLimitParameter", "MaxOutputTokens", "ToolCalling", "ReasoningEffort", "Api");
        fields!.Single(f => f.Name == "OutputLimitParameter").Options.Should().Contain("max_completion_tokens");
    }
}
