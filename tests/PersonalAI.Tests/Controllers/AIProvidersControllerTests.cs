using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Controllers;

public class AIProvidersControllerTests
{
    private sealed class StubProvider(string key, IReadOnlyList<ProviderConfigField> fields) : IAIProvider
    {
        public string Key => key;
        public string DisplayName => key;
        public IReadOnlyList<ProviderConfigField> ConfigFields => fields;
        public IAsyncEnumerable<AiChatChunk> ChatStreamAsync(string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools, int? numCtx, bool? think, string? reasoningEffort, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly IReadOnlyList<ProviderConfigField> OpenAiFields =
    [
        new ProviderConfigField("ApiKey", "API Key", ProviderConfigFieldType.Secret, Required: true),
        new ProviderConfigField("BaseUrl", "Base URL", ProviderConfigFieldType.Url, Required: false, DefaultValue: "https://api.openai.com/v1"),
        AIProviderConfigCache.MaxOutputTokensConfigField
    ];

    private static (AIProvidersController Controller, AppDbContext Db, AIProviderConfigCache Cache) CreateController()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repo = new AIProviderConfigRepository(db);
        var registry = new AIProviderRegistry([new StubProvider("openai", OpenAiFields)]);
        var cache = new AIProviderConfigCache();
        return (new AIProvidersController(registry, repo, cache), db, cache);
    }

    [Fact]
    public async Task GetAll_NoConfigYet_ReportsNotConfiguredAndNoSecretValue()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.GetAll(CancellationToken.None);
        var providers = ((OkObjectResult)result.Result!).Value as IReadOnlyList<AIProviderDto>;

        var openai = providers!.Single(p => p.Key == "openai");
        openai.IsConfigured.Should().BeFalse();
        openai.Fields.Single(f => f.Name == "ApiKey").HasValue.Should().BeFalse();
        openai.Fields.Single(f => f.Name == "ApiKey").Value.Should().BeNull();
    }

    [Fact]
    public async Task Update_SetsRequiredSecretField_BecomesConfigured()
    {
        var (controller, db, cache) = CreateController();
        await using var _1 = db;

        var result = await controller.Update("openai", new UpdateAIProviderConfigRequest(new Dictionary<string, string?> { ["ApiKey"] = "sk-abc" }), CancellationToken.None);
        var dto = ((OkObjectResult)result.Result!).Value as AIProviderDto;

        dto!.IsConfigured.Should().BeTrue();
        dto.Fields.Single(f => f.Name == "ApiKey").HasValue.Should().BeTrue();
        dto.Fields.Single(f => f.Name == "ApiKey").Value.Should().BeNull("secret values are never echoed back");
        cache.GetValue("openai", "ApiKey").Should().Be("sk-abc");
    }

    [Fact]
    public async Task Update_BlankSecretValue_LeavesPreviouslySetKeyUntouched()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        await controller.Update("openai", new UpdateAIProviderConfigRequest(new Dictionary<string, string?> { ["ApiKey"] = "sk-abc" }), CancellationToken.None);
        var result = await controller.Update("openai", new UpdateAIProviderConfigRequest(new Dictionary<string, string?> { ["ApiKey"] = "", ["BaseUrl"] = "https://custom" }), CancellationToken.None);
        var dto = ((OkObjectResult)result.Result!).Value as AIProviderDto;

        dto!.Fields.Single(f => f.Name == "ApiKey").HasValue.Should().BeTrue("a blank secret in the request must not clear the stored key");
        dto.Fields.Single(f => f.Name == "BaseUrl").Value.Should().Be("https://custom");
    }

    [Fact]
    public async Task Update_MissingRequiredField_ReturnsBadRequest()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Update("openai", new UpdateAIProviderConfigRequest(new Dictionary<string, string?> { ["BaseUrl"] = "https://custom" }), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_UnknownField_ReturnsBadRequest()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Update("openai", new UpdateAIProviderConfigRequest(new Dictionary<string, string?> { ["NotAField"] = "x" }), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_UnknownProvider_ReturnsNotFound()
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Update("does-not-exist", new UpdateAIProviderConfigRequest(new Dictionary<string, string?>()), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Theory]
    [InlineData("16k")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("2000000")]
    public async Task Update_InvalidMaxOutputTokens_ReturnsBadRequest(string value)
    {
        var (controller, db, cache) = CreateController();
        await using var _1 = db;

        var result = await controller.Update("openai", new UpdateAIProviderConfigRequest(new Dictionary<string, string?>
        {
            ["ApiKey"] = "sk", ["MaxOutputTokens"] = value
        }), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        cache.GetValue("openai", "MaxOutputTokens").Should().BeNull();
    }

    [Theory]
    [InlineData("32000")]
    [InlineData("")]
    public async Task Update_ValidOrBlankMaxOutputTokens_IsAccepted(string value)
    {
        var (controller, db, _) = CreateController();
        await using var _1 = db;

        var result = await controller.Update("openai", new UpdateAIProviderConfigRequest(new Dictionary<string, string?>
        {
            ["ApiKey"] = "sk", ["MaxOutputTokens"] = value
        }), CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }
}
