using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Sessions;

namespace PersonalAI.Tests.Controllers;

public class SessionsControllerTests
{
    private sealed class StubProvider(string key) : IAIProvider
    {
        public string Key => key;
        public string DisplayName => key;
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public IReadOnlyList<ProviderConfigField> ModelSettingFields => [];
        public IAsyncEnumerable<AiChatChunk> ChatStreamAsync(string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools, int? numCtx, bool? think, string? reasoningEffort, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (SessionsController Controller, ChatSessionRepository Repo, AppDbContext Db) CreateController()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repo = new ChatSessionRepository(db);
        var controller = new SessionsController(
            repo, Mock.Of<IChatTurnRunner>(), new AIProviderRegistry([new StubProvider("ollama"), new StubProvider("openai")]));
        return (controller, repo, db);
    }

    private static UpdateSessionSettingsRequest Settings(string? providerKey = null, string? modelName = null) =>
        new(true, null, true, null, null, [], ToolFlags.Default, null, providerKey, modelName);

    [Fact]
    public async Task UpdateSessionSettings_SwitchesProviderAndModel()
    {
        var (controller, repo, db) = CreateController();
        await using var _ = db;
        var session = await repo.CreateSessionAsync("Chat", "llama3.1:8b", CancellationToken.None);

        var result = await controller.UpdateSessionSettings(session.Id, Settings(" openai ", "gpt-5"), CancellationToken.None);

        var dto = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<SessionSettingsDto>().Subject;
        dto.ProviderKey.Should().Be("openai");
        dto.ModelName.Should().Be("gpt-5");
    }

    [Fact]
    public async Task UpdateSessionSettings_WithoutModel_KeepsCurrentModel()
    {
        var (controller, repo, db) = CreateController();
        await using var _ = db;
        var session = await repo.CreateSessionAsync("Chat", "llama3.1:8b", CancellationToken.None);

        var result = await controller.UpdateSessionSettings(session.Id, Settings(), CancellationToken.None);

        var dto = result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<SessionSettingsDto>().Subject;
        dto.ProviderKey.Should().Be("ollama");
        dto.ModelName.Should().Be("llama3.1:8b");
    }

    [Theory]
    [InlineData("nope", "gpt-5")]
    [InlineData("openai", null)]
    [InlineData(null, "  ")]
    public async Task UpdateSessionSettings_RejectsInvalidModelChange(string? providerKey, string? modelName)
    {
        var (controller, repo, db) = CreateController();
        await using var _ = db;
        var session = await repo.CreateSessionAsync("Chat", "llama3.1:8b", CancellationToken.None);

        var result = await controller.UpdateSessionSettings(session.Id, Settings(providerKey, modelName), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.ChatSessions.FindAsync(session.Id))!.ModelName.Should().Be("llama3.1:8b");
    }
}
