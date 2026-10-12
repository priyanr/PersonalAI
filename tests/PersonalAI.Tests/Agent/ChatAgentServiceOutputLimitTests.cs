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

/// <summary>A model that returns nothing usable — typically a reasoning model that spends its whole output
/// budget thinking and hits max_tokens — must end the turn with a visible notice, not a silent blank answer.</summary>
public class ChatAgentServiceOutputLimitTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ChatAgentService CreateService(AppDbContext db, FakeAIProvider provider, ToolRegistry? tools = null) => new(
        new ChatSessionRepository(db),
        new SkillsRepository(db),
        new FakeSkillExecutionService((_, _) => []),
        new AIProviderRegistry([(IAIProvider)provider]),
        tools ?? new ToolRegistry([]),
        Options.Create(new OllamaSettings { BaseUrl = "http://localhost:11434", DefaultSystemPrompt = "system" }),
        Options.Create(new AgentSettings { MaxIterations = 25 }),
        new AppSettingsCache());

    private static async Task<List<AgentStreamEvent>> RunAsync(ChatAgentService service, Guid sessionId, string text = "Hi")
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(sessionId, text, CancellationToken.None))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task ReasoningOnlyResponseCutOffAtOutputLimit_EndsTurnWithNoticeInsteadOfBlankAnswer()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "deepseek-flash", CancellationToken.None);

        var provider = new FakeAIProvider([
            new AiChatChunk(null, null, false, ReasoningDelta: "Let me plan the whole app..."),
            new AiChatChunk(null, null, true, new AiTokenUsage(5000, 8192), HitOutputLimit: true)
        ]);
        var service = CreateService(db, provider);

        var events = await RunAsync(service, session.Id);

        events.OfType<ReasoningDeltaEvent>().Should().ContainSingle(e => e.Text == "Let me plan the whole app...");
        events.Should().NotContain(e => e is DoneEvent);
        var error = events.OfType<ErrorEvent>().Should().ContainSingle().Subject;
        error.Message.Should().Contain("thinking").And.Contain("8192").And.Contain("Max output tokens");
        provider.RecordedRequests.Should().HaveCount(1, "a retry would most likely burn the whole budget again");

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Should().NotContain(m => m.Role == MessageRole.Assistant, "a blank answer is not persisted");
        messages[^1].Role.Should().Be(MessageRole.System);
        messages[^1].Content.Should().Be(error.Message);
    }

    [Fact]
    public async Task EmptyResponseWithoutOutputLimit_EndsTurnWithEmptyResponseNotice()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var service = CreateService(db, new FakeAIProvider([new AiChatChunk("  ", null, true)]));

        var events = await RunAsync(service, session.Id);

        events.OfType<ErrorEvent>().Should().ContainSingle(e => e.Message.Contains("empty response"));
        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Should().NotContain(m => m.Role == MessageRole.Assistant);
        messages[^1].Role.Should().Be(MessageRole.System);
    }

    [Fact]
    public async Task TextAnswerCutOffAtOutputLimit_KeepsPartialAnswerAndAddsCutOffNotice()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var service = CreateService(db, new FakeAIProvider([
            new AiChatChunk("Here is a very long answer", null, false),
            new AiChatChunk(null, null, true, HitOutputLimit: true)
        ]));

        var events = await RunAsync(service, session.Id);

        events.OfType<ErrorEvent>().Should().ContainSingle(e => e.Message.Contains("cut off"));
        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages[^2].Role.Should().Be(MessageRole.Assistant);
        messages[^2].Content.Should().Be("Here is a very long answer");
        messages[^1].Role.Should().Be(MessageRole.System);
    }

    [Fact]
    public async Task ToolCallTruncatedByOutputLimit_ToldToSplitTheCallAndLoopContinues()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk(null, [new AiToolCall("call1", "test_tool", """{"content":"partial fi""")], true, HitOutputLimit: true)],
            [new AiChatChunk("Done", null, true)]);
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("never runs"))]);
        var service = CreateService(db, provider, tools);

        var events = await RunAsync(service, session.Id);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("output-token limit"));
        events.Should().ContainSingle(e => e is DoneEvent);
    }

    [Fact]
    public async Task NormalAnswerWithReasoning_StreamsReasoningButPersistsOnlyTheAnswer()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "qwen3:8b", CancellationToken.None);

        var service = CreateService(db, new FakeAIProvider([
            new AiChatChunk(null, null, false, ReasoningDelta: "hmm"),
            new AiChatChunk("Answer", null, true)
        ]));

        var events = await RunAsync(service, session.Id);

        events.OfType<ReasoningDeltaEvent>().Should().ContainSingle();
        events.Should().ContainSingle(e => e is DoneEvent);
        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages[^1].Content.Should().Be("Answer");
    }

    [Fact]
    public async Task OutputLimitNotice_IsShownButNeverSentBackToTheModel()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk(null, null, true, HitOutputLimit: true)],
            [new AiChatChunk("Answer", null, true)]);
        var service = CreateService(db, provider);

        await RunAsync(service, session.Id, "first");
        await RunAsync(service, session.Id, "second");

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Should().ContainSingle(m => m.Role == MessageRole.System && m.ExcludeFromContext);
        provider.RecordedRequests[1].Should().NotContain(m => m.Content != null && m.Content.Contains("Max output tokens"));
        provider.RecordedRequests[1].Should().Contain(m => m.Role == "user" && m.Content == "first");
    }

    [Fact]
    public async Task ProviderWithoutThinkingToggle_AdviceDoesNotSuggestTurningThinkingOff()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var service = CreateService(db, new FakeAIProvider([new AiChatChunk(null, null, true, HitOutputLimit: true)]));

        var events = await RunAsync(service, session.Id);

        events.OfType<ErrorEvent>().Single().Message.Should().NotContain("thinking off");
    }

    [Fact]
    public async Task ToolCallCutOffBeforeItsArguments_IsNotRunAndModelIsToldToSplitIt()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ran = false;
        var provider = new FakeAIProvider(
            [new AiChatChunk(null, [new AiToolCall("call1", "test_tool", "")], true, HitOutputLimit: true)],
            [new AiChatChunk("Done", null, true)]);
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => { ran = true; return new ToolCompleted("ran"); })]);
        var service = CreateService(db, provider, tools);

        var events = await RunAsync(service, session.Id);

        ran.Should().BeFalse();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("before its arguments"));
    }

    [Fact]
    public async Task ReasoningBehindAToolCall_IsPassedBackWithinTheTurnOnly()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var provider = new FakeAIProvider(
            [
                new AiChatChunk(null, null, false, ReasoningDelta: "need the tool"),
                new AiChatChunk(null, [new AiToolCall("call1", "test_tool", "{}")], true)
            ],
            [new AiChatChunk("Done", null, true)],
            [new AiChatChunk("Next", null, true)]);
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("result"))]);
        var service = CreateService(db, provider, tools);

        await RunAsync(service, session.Id, "first");
        await RunAsync(service, session.Id, "second");

        provider.RecordedRequests[1].Should().ContainSingle(m => m.Role == "assistant" && m.ReasoningContent == "need the tool");
        provider.RecordedRequests[2].Should().OnlyContain(m => m.ReasoningContent == null);
    }

    [Fact]
    public async Task ChatTurn_PassesTheSessionsThinkingPreference()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var provider = new FakeAIProvider([new AiChatChunk("Hi", null, true)]);
        var service = CreateService(db, provider);

        await RunAsync(service, session.Id);

        provider.RecordedThink.Should().Equal([true], "the session inherits AppSettings.ThinkingEnabled (default on)");
    }

    [Fact]
    public async Task ChatTurn_PassesTheSessionsReasoningEffort()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "gpt-oss:20b", CancellationToken.None, reasoningEffort: "high");

        var provider = new FakeAIProvider([new AiChatChunk("Hi", null, true)]);
        var service = CreateService(db, provider);

        await RunAsync(service, session.Id);

        provider.RecordedReasoningEffort.Should().Equal(["high"]);
    }
}
