using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.Sessions;

namespace PersonalAI.Tests.Services;

public class ChatSessionsServiceTests
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

    private static (ChatSessionsService Service, AppDbContext Db) CreateService()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new ChatSessionsService(
            db, new ChatSessionRepository(db), new ChatTurnRunner(Mock.Of<IServiceScopeFactory>(), NullLogger<ChatTurnRunner>.Instance),
            new AIProviderRegistry([new StubProvider("ollama"), new StubProvider("openai")]));
        return (service, db);
    }

    private static UpdateSessionSettingsRequest Settings(string? providerKey = null, string? modelName = null) =>
        new(true, null, true, null, null, [], ToolFlags.Default, null, providerKey, modelName);

    [Fact]
    public async Task Create_StoresTheSessionWithItsToolFlags()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var flags = ToolFlags.Default with { GmailEnabled = true, WebSearchEnabled = false };

        var created = await service.Create(new CreateSessionRequest("My Chat", "llama3.1:8b", ToolFlags: flags, ReasoningEffort: " HIGH "), CancellationToken.None);

        var detail = await service.GetSession(created.Id, CancellationToken.None);
        detail.Title.Should().Be("My Chat");
        detail.ToolFlags.Should().Be(flags);
        detail.ReasoningEffort.Should().Be("high");
        detail.IsTurnRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Create_WithoutModel_IsInvalid()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.Create(new CreateSessionRequest(null, " "), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Theory]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task Rename_BlankTitle_IsInvalid(string? title)
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var session = await TestSeed.SessionAsync(db, "Old", "llama3.1:8b", CancellationToken.None);

        var act = () => service.Rename(session.Id, new RenameSessionRequest(title), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Rename_TooLongTitle_IsInvalid()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var session = await TestSeed.SessionAsync(db, "Old", "llama3.1:8b", CancellationToken.None);

        var act = () => service.Rename(session.Id, new RenameSessionRequest(new string('x', ChatSessionsService.MaxTitleLength + 1)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
    }

    [Fact]
    public async Task Rename_UpdatesTitle_AndUnknownSessionIsNotFound()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var session = await TestSeed.SessionAsync(db, "Old", "llama3.1:8b", CancellationToken.None);

        var renamed = await service.Rename(session.Id, new RenameSessionRequest(" New "), CancellationToken.None);
        var act = () => service.Rename(Guid.NewGuid(), new RenameSessionRequest("New"), CancellationToken.None);

        renamed.Title.Should().Be("New");
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task UpdateSettings_SwitchesProviderAndModel()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var session = await TestSeed.SessionAsync(db, "Chat", "llama3.1:8b", CancellationToken.None);

        var dto = await service.UpdateSettings(session.Id, Settings(" openai ", "gpt-5"), CancellationToken.None);

        dto.ProviderKey.Should().Be("openai");
        dto.ModelName.Should().Be("gpt-5");
    }

    [Fact]
    public async Task UpdateSettings_WithoutModel_KeepsCurrentModel()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var session = await TestSeed.SessionAsync(db, "Chat", "llama3.1:8b", CancellationToken.None);

        var dto = await service.UpdateSettings(session.Id, Settings(), CancellationToken.None);

        dto.ProviderKey.Should().Be("ollama");
        dto.ModelName.Should().Be("llama3.1:8b");
    }

    [Theory]
    [InlineData("nope", "gpt-5")]
    [InlineData("openai", null)]
    [InlineData(null, "  ")]
    public async Task UpdateSettings_RejectsInvalidModelChange(string? providerKey, string? modelName)
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var session = await TestSeed.SessionAsync(db, "Chat", "llama3.1:8b", CancellationToken.None);

        var act = () => service.UpdateSettings(session.Id, Settings(providerKey, modelName), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>();
        (await db.ChatSessions.FindAsync(session.Id))!.ModelName.Should().Be("llama3.1:8b");
    }

    [Fact]
    public async Task StartMessage_UnknownSession_IsNotFound()
    {
        var (service, db) = CreateService();
        await using var _ = db;

        var act = () => service.StartMessage(Guid.NewGuid(), "hi", CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task StartApprovalResolution_NotPending_Conflicts()
    {
        var (service, db) = CreateService();
        await using var _ = db;
        var session = await TestSeed.SessionAsync(db, "Chat", "m", CancellationToken.None);
        var invocation = await TestSeed.ToolInvocationAsync(db, new Core.Models.Entities.ToolInvocation
        {
            SessionId = session.Id, RequestingMessageId = Guid.NewGuid(), ToolCallId = "c1", ToolName = "write_file", ArgumentsJson = "{}",
            Status = Core.Models.Entities.Enums.ToolInvocationStatus.Approved
        }, CancellationToken.None);

        var act = () => service.StartApprovalResolution(invocation.Id, true, CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }
}
