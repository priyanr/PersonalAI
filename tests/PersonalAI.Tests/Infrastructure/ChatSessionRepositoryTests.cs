using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Sessions;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Infrastructure;

public class ChatSessionRepositoryTests
{
    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task CreateSessionAsync_ThenGetSessionsAsync_ReturnsCreatedSession()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);

        var session = await repo.CreateSessionAsync("My Chat", "llama3.1:8b", CancellationToken.None);
        var (sessions, total) = await repo.GetSessionsAsync(null, 1, 20, CancellationToken.None);

        sessions.Should().ContainSingle(s => s.Id == session.Id && s.Title == "My Chat" && s.ModelName == "llama3.1:8b");
        total.Should().Be(1);
    }

    [Fact]
    public async Task GetSessionsAsync_FiltersByTitleCaseInsensitively()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var match = await repo.CreateSessionAsync("Docker networking help", "llama3.1:8b", CancellationToken.None);
        await repo.CreateSessionAsync("Recipe ideas", "llama3.1:8b", CancellationToken.None);
        await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var (sessions, total) = await repo.GetSessionsAsync("  DOCKER ", 1, 20, CancellationToken.None);

        sessions.Should().ContainSingle(s => s.Id == match.Id);
        total.Should().Be(1);
    }

    [Fact]
    public async Task GetSessionsAsync_PagesNewestFirstAndReportsTotalCount()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var s = await repo.CreateSessionAsync($"Chat {i}", "llama3.1:8b", CancellationToken.None);
            s.UpdatedAt = baseTime.AddMinutes(i);
        }
        await db.SaveChangesAsync();

        var (page1, total) = await repo.GetSessionsAsync(null, 1, 2, CancellationToken.None);
        var (page3, _) = await repo.GetSessionsAsync(null, 3, 2, CancellationToken.None);

        total.Should().Be(5);
        page1.Select(s => s.Title).Should().Equal("Chat 4", "Chat 3");
        page3.Select(s => s.Title).Should().Equal("Chat 0");
    }

    [Fact]
    public async Task SetSessionTitleAsync_UpdatesTitle_AndReturnsNullForUnknownSession()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync("Old", "llama3.1:8b", CancellationToken.None);

        var renamed = await repo.SetSessionTitleAsync(session.Id, "New", CancellationToken.None);
        var missing = await repo.SetSessionTitleAsync(Guid.NewGuid(), "New", CancellationToken.None);

        renamed!.Title.Should().Be("New");
        missing.Should().BeNull();
    }

    [Fact]
    public async Task AppendMessageAsync_AssignsSequentialSequenceNumbers()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var m1 = await repo.AppendMessageAsync(new ChatMessage { SessionId = session.Id, Role = MessageRole.User, Content = "hi" }, CancellationToken.None);
        var m2 = await repo.AppendMessageAsync(new ChatMessage { SessionId = session.Id, Role = MessageRole.Assistant, Content = "hello" }, CancellationToken.None);
        var m3 = await repo.AppendMessageAsync(new ChatMessage { SessionId = session.Id, Role = MessageRole.User, Content = "thanks" }, CancellationToken.None);

        m1.SequenceNumber.Should().Be(1);
        m2.SequenceNumber.Should().Be(2);
        m3.SequenceNumber.Should().Be(3);
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsMessagesOrderedBySequenceNumber()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        await repo.AppendMessageAsync(new ChatMessage { SessionId = session.Id, Role = MessageRole.User, Content = "first" }, CancellationToken.None);
        await repo.AppendMessageAsync(new ChatMessage { SessionId = session.Id, Role = MessageRole.Assistant, Content = "second" }, CancellationToken.None);

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);

        messages.Should().HaveCount(2);
        messages[0].Content.Should().Be("first");
        messages[1].Content.Should().Be("second");
    }

    [Fact]
    public async Task ToolCallsJson_SurvivesReload()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        const string toolCallsJson = """[{"ToolCallId":"abc","Name":"web_search","ArgumentsJson":"{\"query\":\"weather\"}"}]""";
        await repo.AppendMessageAsync(new ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.Assistant,
            Content = "",
            ToolCallsJson = toolCallsJson
        }, CancellationToken.None);

        var reloaded = await repo.GetSessionAsync(session.Id, CancellationToken.None);

        reloaded!.Messages.Single().ToolCallsJson.Should().Be(toolCallsJson);
    }

    [Fact]
    public async Task PendingApprovals_TrackedByRequestingMessage()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);
        var assistantMessage = await repo.AppendMessageAsync(
            new ChatMessage { SessionId = session.Id, Role = MessageRole.Assistant, Content = "" }, CancellationToken.None);

        var invocation = await repo.CreateToolInvocationAsync(new ToolInvocation
        {
            SessionId = session.Id,
            RequestingMessageId = assistantMessage.Id,
            ToolCallId = "call-1",
            ToolName = "write_file",
            ArgumentsJson = "{}",
            Status = ToolInvocationStatus.PendingApproval
        }, CancellationToken.None);

        (await repo.HasPendingApprovalsAsync(session.Id, CancellationToken.None)).Should().BeTrue();
        (await repo.GetPendingApprovalsAsync(assistantMessage.Id, CancellationToken.None)).Should().ContainSingle(t => t.Id == invocation.Id);

        invocation.Status = ToolInvocationStatus.Approved;
        await repo.UpdateToolInvocationAsync(invocation, CancellationToken.None);

        (await repo.HasPendingApprovalsAsync(session.Id, CancellationToken.None)).Should().BeFalse();
    }
}
