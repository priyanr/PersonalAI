using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Sessions;
using PersonalAI.Core.Services.Settings;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Agent;

/// <summary>What a turn leaves behind when it's stopped, fails, or the server shuts down mid-turn —
/// the transcript half of ChatTurnRunner's contract (the runner half is in ChatTurnRunnerTests).</summary>
public class ChatAgentServiceInterruptionTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ChatAgentService CreateService(AppDbContext db, FakeAIProvider provider, ToolRegistry tools) => new(
        db,
        new ChatSessionRepository(db),
        new SkillsRepository(db),
        new FakeSkillProvider().CreateService(db),
        new ChatHistoryCompaction(db, new AIProviderRegistry([(IAIProvider)provider]), Options.Create(new AgentSettings { MaxIterations = 25 })),
        new AIProviderRegistry([(IAIProvider)provider]),
        tools,
        Options.Create(new OllamaSettings { BaseUrl = "http://localhost:11434", DefaultSystemPrompt = "system" }),
        Options.Create(new AgentSettings { MaxIterations = 25 }),
        new AppSettingsCache());

    /// <summary>Signals once it starts, then waits until cancelled — a stand-in for a sandbox call
    /// waiting on its host to boot.</summary>
    private sealed class BlockingTool(string name) : ITool
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => name;
        public string Description => "blocks until cancelled";
        public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

        public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ToolCompleted("unreachable");
        }
    }

    [Fact]
    public async Task RunTurnAsync_FirstEventIsTurnStarted_CarryingTheUserMessageSequenceNumber()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await TestSeed.SessionAsync(db, null, "m", CancellationToken.None);
        var service = CreateService(db, new FakeAIProvider([new AiChatChunk("Hi", null, true)]), new ToolRegistry([]));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Hello", CancellationToken.None))
            events.Add(e);

        var userMessage = (await repo.GetMessagesAsync(session.Id, CancellationToken.None))[0];
        events[0].Should().Be(new TurnStartedEvent(userMessage.SequenceNumber));
    }

    [Fact]
    public async Task CancelledMidTool_ThenFinalized_LeavesAnInterruptedResultAndNotice()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await TestSeed.SessionAsync(db, null, "m", CancellationToken.None);
        var tool = new BlockingTool("slow_tool");
        var provider = new FakeAIProvider([new AiChatChunk("", [new AiToolCall("call1", "slow_tool", "{}")], true)]);
        var service = CreateService(db, provider, new ToolRegistry([tool]));

        using var cts = new CancellationTokenSource();
        var run = Task.Run(async () =>
        {
            await foreach (var _ in service.RunTurnAsync(session.Id, "Do it", cts.Token)) { }
        });
        await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        // Must end promptly (no spin/hang on the cancelled progress wait) and surface as cancellation.
        await FluentActions.Awaiting(() => run.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<OperationCanceledException>();

        await service.FinalizeInterruptedTurnAsync(session.Id, null, "stopped by the user", "Turn interrupted: stopped by the user.", CancellationToken.None);

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Select(m => m.Role).Should().Equal(MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.System);
        messages[2].ToolCallId.Should().Be("call1");
        messages[2].IsError.Should().BeTrue();
        messages[2].Content.Should().Contain("stopped by the user");
        messages[3].Content.Should().Be("Turn interrupted: stopped by the user.");
    }

    [Fact]
    public async Task FinalizeInterruptedTurnAsync_PersistsPartialAssistantText()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await TestSeed.SessionAsync(db, null, "m", CancellationToken.None);
        await TestSeed.MessageAsync(db, new() { SessionId = session.Id, Role = MessageRole.User, Content = "q" }, CancellationToken.None);
        var service = CreateService(db, new FakeAIProvider(), new ToolRegistry([]));

        await service.FinalizeInterruptedTurnAsync(session.Id, "Half an ans", "stopped by the user", "Turn interrupted: stopped by the user.", CancellationToken.None);

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Select(m => (m.Role, m.Content)).Should().Equal(
            (MessageRole.User, "q"),
            (MessageRole.Assistant, "Half an ans"),
            (MessageRole.System, "Turn interrupted: stopped by the user."));
    }

    [Fact]
    public async Task FinalizeInterruptedTurnAsync_LeavesPendingApprovalsAndAnsweredCallsAlone()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await TestSeed.SessionAsync(db, null, "m", CancellationToken.None);
        var calls = new List<AiToolCall> { new("done", "t", "{}"), new("pending", "t", "{}"), new("orphan", "t", "{}") };
        var assistant = await TestSeed.MessageAsync(db, new()
        {
            SessionId = session.Id, Role = MessageRole.Assistant, Content = "", ToolCallsJson = JsonSerializer.Serialize(calls)
        }, CancellationToken.None);
        await TestSeed.MessageAsync(db, new() { SessionId = session.Id, Role = MessageRole.Tool, ToolCallId = "done", ToolName = "t", Content = "ok" }, CancellationToken.None);
        await TestSeed.ToolInvocationAsync(db, new()
        {
            SessionId = session.Id, RequestingMessageId = assistant.Id, ToolCallId = "pending", ToolName = "t",
            ArgumentsJson = "{}", Status = ToolInvocationStatus.PendingApproval, ResolvedPath = "/x", Reason = "r"
        }, CancellationToken.None);
        var service = CreateService(db, new FakeAIProvider(), new ToolRegistry([]));

        await service.FinalizeInterruptedTurnAsync(session.Id, null, "the server shut down", null, CancellationToken.None);

        var toolMessages = (await repo.GetMessagesAsync(session.Id, CancellationToken.None)).Where(m => m.Role == MessageRole.Tool).ToList();
        toolMessages.Select(m => m.ToolCallId).Should().Equal("done", "orphan");
        toolMessages[1].Content.Should().Contain("the server shut down");
        (await repo.GetSessionAsync(session.Id, CancellationToken.None))!.Status.Should().Be(SessionStatus.AwaitingApproval);
    }
}
