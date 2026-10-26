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
    public async Task GetSessionsAsync_FiltersByTitleCaseInsensitively()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var match = await TestSeed.SessionAsync(db, "Docker networking help", "llama3.1:8b", CancellationToken.None);
        await TestSeed.SessionAsync(db, "Recipe ideas", "llama3.1:8b", CancellationToken.None);
        await TestSeed.SessionAsync(db, null, "llama3.1:8b", CancellationToken.None);

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
            var s = await TestSeed.SessionAsync(db, $"Chat {i}", "llama3.1:8b", CancellationToken.None);
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
    public async Task GetMessagesAsync_ReturnsMessagesOrderedBySequenceNumber()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await TestSeed.SessionAsync(db, null, "llama3.1:8b", CancellationToken.None);
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, SequenceNumber = 2, Role = MessageRole.Assistant, Content = "second" });
        db.ChatMessages.Add(new ChatMessage { SessionId = session.Id, SequenceNumber = 1, Role = MessageRole.User, Content = "first" });
        await db.SaveChangesAsync();

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);

        messages.Select(m => m.Content).Should().Equal("first", "second");
    }

    [Fact]
    public async Task ToolCallsJson_SurvivesReload()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await TestSeed.SessionAsync(db, null, "llama3.1:8b", CancellationToken.None);

        const string toolCallsJson = """[{"ToolCallId":"abc","Name":"web_search","ArgumentsJson":"{\"query\":\"weather\"}"}]""";
        await TestSeed.MessageAsync(db, new ChatMessage
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
        var session = await TestSeed.SessionAsync(db, null, "llama3.1:8b", CancellationToken.None);
        var assistantMessage = await TestSeed.MessageAsync(db,
            new ChatMessage { SessionId = session.Id, Role = MessageRole.Assistant, Content = "" }, CancellationToken.None);

        var invocation = await TestSeed.ToolInvocationAsync(db, new ToolInvocation
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
        await db.SaveChangesAsync();

        (await repo.HasPendingApprovalsAsync(session.Id, CancellationToken.None)).Should().BeFalse();
    }
}
