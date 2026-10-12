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

public class ChatAgentServiceTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ChatAgentService CreateService(
        AppDbContext db, FakeAIProvider ollama, ToolRegistry tools, AgentSettings? agentSettings = null,
        FakeSkillExecutionService? skillExecution = null) => new(
        new ChatSessionRepository(db),
        new SkillsRepository(db),
        skillExecution ?? new FakeSkillExecutionService((_, _) => []),
        new AIProviderRegistry([(IAIProvider)ollama]),
        tools,
        Options.Create(new OllamaSettings { BaseUrl = "http://localhost:11434", DefaultSystemPrompt = "system" }),
        Options.Create(agentSettings ?? new AgentSettings { MaxIterations = 25 }),
        new AppSettingsCache());

    [Fact]
    public async Task RunTurnAsync_PlainTextTurn_EndsImmediatelyWithDone()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider([
            new AiChatChunk("Hello", null, false),
            new AiChatChunk(" world", null, true)
        ]);
        var service = CreateService(db, ollama, new ToolRegistry([]));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None))
            events.Add(e);

        events.OfType<TextDeltaEvent>().Select(e => e.Text).Should().Equal("Hello", " world");
        events.Should().ContainSingle(e => e is DoneEvent);

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(MessageRole.User);
        messages[1].Content.Should().Be("Hello world");
    }

    [Fact]
    public async Task RunTurnAsync_ToolCallTurn_ExecutesToolThenLoopsToFinalAnswer()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "test_tool", "{}")], true)],
            [new AiChatChunk("Done!", null, true)]);

        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("tool result content"))]);
        var service = CreateService(db, ollama, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Search something", CancellationToken.None))
            events.Add(e);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.ResultContent == "tool result content" && !e.IsError);
        events.Should().ContainSingle(e => e is DoneEvent);
        ollama.RecordedRequests.Should().HaveCount(2);
        ollama.RecordedRequests[1].Should().Contain(m => m.Role == "tool" && m.Content == "tool result content");
    }

    [Fact]
    public async Task RunTurnAsync_HistoryHasAssistantToolCallMissingItsToolResponse_SynthesizesPlaceholderBeforeSending()
    {
        // Simulates a prior turn whose request was cancelled mid-loop (client disconnect, server
        // restart) after the Assistant tool_calls message was persisted but before every matching Tool
        // result was appended — call2 never got a response. Every OpenAI-compatible provider (including
        // Azure deployments that aren't OpenAI itself) rejects the whole request with a 400 if even one
        // tool_call_id from a preceding Assistant message has no Tool-role response, so this must be
        // repaired before the next turn's request goes out.
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.Assistant,
            Content = "",
            ToolCallsJson = System.Text.Json.JsonSerializer.Serialize(new List<AiToolCall>
            {
                new("call1", "test_tool", "{}"),
                new("call2", "test_tool", "{}")
            })
        }, CancellationToken.None);
        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.Tool,
            ToolCallId = "call1",
            ToolName = "test_tool",
            Content = "call1 result"
        }, CancellationToken.None);
        // call2's Tool response is missing entirely — the interrupted-turn scenario.

        var ollama = new FakeAIProvider([new AiChatChunk("Continuing", null, true)]);
        var service = CreateService(db, ollama, new ToolRegistry([]));

        await foreach (var _ in service.RunTurnAsync(session.Id, "next message", CancellationToken.None)) { }

        ollama.RecordedRequests.Should().HaveCount(1);
        var sent = ollama.RecordedRequests[0];
        sent.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call1" && m.Content == "call1 result");
        sent.Should().ContainSingle(m => m.Role == "tool" && m.ToolCallId == "call2");
        // The synthesized message must sit directly after call1's, immediately following the Assistant
        // tool_calls message and before the next user message — not appended at the end of history.
        var assistantIndex = sent.ToList().FindIndex(m => m.Role == "assistant" && m.ToolCalls is { Count: 2 });
        sent[assistantIndex + 1].ToolCallId.Should().Be("call1");
        sent[assistantIndex + 2].ToolCallId.Should().Be("call2");
        sent[assistantIndex + 3].Role.Should().Be("user");
    }

    [Fact]
    public async Task RunTurnAsync_ApprovalRequiredTool_HaltsWithoutCallingOllamaAgain()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "gated_tool", "{}")], true)]);

        var tools = new ToolRegistry([new FakeTool("gated_tool", (_, ctx) =>
            ctx.PreApproved ? new ToolCompleted("approved-result") : new ToolApprovalRequired("needs approval", "/outside/path"))]);
        var service = CreateService(db, ollama, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Write outside", CancellationToken.None))
            events.Add(e);

        events.Should().ContainSingle(e => e is ApprovalRequiredEvent);
        ollama.RecordedRequests.Should().HaveCount(1);

        var session2 = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        session2!.Status.Should().Be(SessionStatus.AwaitingApproval);
        (await repo.HasPendingApprovalsAsync(session.Id, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ResolveApprovalAsync_Approved_ExecutesToolThenContinuesTurn()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "gated_tool", "{}")], true)],
            [new AiChatChunk("All done", null, true)]);

        var tools = new ToolRegistry([new FakeTool("gated_tool", (_, ctx) =>
            ctx.PreApproved ? new ToolCompleted("approved-result") : new ToolApprovalRequired("needs approval", "/outside/path"))]);
        var service = CreateService(db, ollama, tools);

        await foreach (var _ in service.RunTurnAsync(session.Id, "Write outside", CancellationToken.None)) { }

        var pending = (await repo.GetSessionAsync(session.Id, CancellationToken.None))!.ToolInvocations
            .Single(t => t.Status == ToolInvocationStatus.PendingApproval);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.ResolveApprovalAsync(pending.Id, approved: true, CancellationToken.None))
            events.Add(e);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.ResultContent == "approved-result" && !e.IsError);
        events.OfType<TextDeltaEvent>().Select(e => e.Text).Should().Equal("All done");
        events.Should().ContainSingle(e => e is DoneEvent);
        ollama.RecordedRequests.Should().HaveCount(2);

        var session2 = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        session2!.Status.Should().Be(SessionStatus.Active);
    }

    [Fact]
    public async Task ResolveApprovalAsync_Denied_FeedsDenialBackAndContinuesTurn()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "gated_tool", "{}")], true)],
            [new AiChatChunk("Understood, I will not do that.", null, true)]);

        var tools = new ToolRegistry([new FakeTool("gated_tool", (_, ctx) =>
            ctx.PreApproved ? new ToolCompleted("approved-result") : new ToolApprovalRequired("needs approval", "/outside/path"))]);
        var service = CreateService(db, ollama, tools);

        await foreach (var _ in service.RunTurnAsync(session.Id, "Write outside", CancellationToken.None)) { }

        var pending = (await repo.GetSessionAsync(session.Id, CancellationToken.None))!.ToolInvocations
            .Single(t => t.Status == ToolInvocationStatus.PendingApproval);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.ResolveApprovalAsync(pending.Id, approved: false, CancellationToken.None))
            events.Add(e);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError);
        ollama.RecordedRequests[1].Should().Contain(m => m.Role == "tool" && m.Content!.Contains("denied"));
    }

    [Fact]
    public async Task RunTurnAsync_MultiCallBatch_ExecutesAutoApprovedAndPausesOnGated()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
        [
            new AiChatChunk("", [
                new AiToolCall("call1", "auto_tool", "{}"),
                new AiToolCall("call2", "gated_tool", "{}")
            ], true)
        ]);

        var tools = new ToolRegistry([
            new FakeTool("auto_tool", (_, _) => new ToolCompleted("auto result")),
            new FakeTool("gated_tool", (_, ctx) => ctx.PreApproved
                ? new ToolCompleted("gated result")
                : new ToolApprovalRequired("needs approval", "/outside/path"))
        ]);
        var service = CreateService(db, ollama, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Do two things", CancellationToken.None))
            events.Add(e);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.ResultContent == "auto result");
        events.Should().ContainSingle(e => e is ApprovalRequiredEvent);
        ollama.RecordedRequests.Should().HaveCount(1); // paused before a second Ollama call

        var reloaded = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        reloaded!.ToolInvocations.Should().ContainSingle(t => t.ToolName == "auto_tool" && t.Status == ToolInvocationStatus.Completed);
        reloaded.ToolInvocations.Should().ContainSingle(t => t.ToolName == "gated_tool" && t.Status == ToolInvocationStatus.PendingApproval);
    }

    [Fact]
    public async Task RunTurnAsync_ToolsDisabledOnSession_SendsNoToolDefinitionsToOllama()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, toolsEnabled: false);

        var ollama = new FakeAIProvider([new AiChatChunk("Hi there", null, true)]);
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("result"))]);
        var service = CreateService(db, ollama, tools);

        await foreach (var _ in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None)) { }

        ollama.RecordedToolSets.Should().ContainSingle(t => t.Count == 0);
    }

    [Fact]
    public async Task RunTurnAsync_WebSearchDisabledOnSession_OmitsOnlyThatToolDefinition()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var toolFlags = new ToolFlags(
            WebSearchEnabled: false, ReadFileEnabled: true, WriteFileEnabled: true,
            EditFileEnabled: true, ListFilesEnabled: true, SearchFilesEnabled: true, NetworkScanEnabled: false,
            HttpRequestEnabled: true, ComputerControlEnabled: false, SandboxEnabled: false);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, toolFlags: toolFlags);

        var ollama = new FakeAIProvider([new AiChatChunk("Hi there", null, true)]);
        var tools = new ToolRegistry([
            new FakeTool("web_search", (_, _) => new ToolCompleted("result")),
            new FakeTool("read_file", (_, _) => new ToolCompleted("result"))
        ]);
        var service = CreateService(db, ollama, tools);

        await foreach (var _ in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None)) { }

        var sentTools = ollama.RecordedToolSets.Single();
        sentTools.Select(t => t.Name).Should().BeEquivalentTo(["read_file"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTurnAsync_GmailFlag_ControlsWhetherGmailToolIsSent(bool gmailEnabled)
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var toolFlags = ToolFlags.Default with { GmailEnabled = gmailEnabled };
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, toolFlags: toolFlags);

        var ollama = new FakeAIProvider([new AiChatChunk("Hi there", null, true)]);
        var tools = new ToolRegistry([
            new FakeTool("gmail", (_, _) => new ToolCompleted("result")),
            new FakeTool("read_file", (_, _) => new ToolCompleted("result"))
        ]);
        var service = CreateService(db, ollama, tools);

        await foreach (var _ in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None)) { }

        var sentTools = ollama.RecordedToolSets.Single().Select(t => t.Name);
        if (gmailEnabled) sentTools.Should().BeEquivalentTo(["gmail", "read_file"]);
        else sentTools.Should().BeEquivalentTo(["read_file"]);
    }

    [Fact]
    public void ToolFlagsDefault_HasGmailOff()
    {
        ToolFlags.Default.GmailEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task RunTurnAsync_ModelCallsToolDisabledForSession_ReturnsErrorWithoutExecuting()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var toolFlags = new ToolFlags(
            WebSearchEnabled: false, ReadFileEnabled: true, WriteFileEnabled: true,
            EditFileEnabled: true, ListFilesEnabled: true, SearchFilesEnabled: true, NetworkScanEnabled: false,
            HttpRequestEnabled: true, ComputerControlEnabled: false, SandboxEnabled: false);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, toolFlags: toolFlags);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "web_search", "{}")], true)],
            [new AiChatChunk("Done!", null, true)]);

        var executed = false;
        var tools = new ToolRegistry([new FakeTool("web_search", (_, _) => { executed = true; return new ToolCompleted("result"); })]);
        var service = CreateService(db, ollama, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None))
            events.Add(e);

        executed.Should().BeFalse();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent.Contains("disabled"));
    }

    [Fact]
    public async Task RunTurnAsync_SessionHasNumCtx_PassesItToOllama()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, numCtx: 8192);

        var ollama = new FakeAIProvider([new AiChatChunk("Hi there", null, true)]);
        var service = CreateService(db, ollama, new ToolRegistry([]));

        await foreach (var _ in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None)) { }

        ollama.RecordedNumCtx.Should().Equal(8192);
    }

    [Fact]
    public async Task RunTurnAsync_SessionMaxLlmCallsSet_OverridesGlobalDefaultAndCapsAtThatMany()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        // Global default is 25, but this session's own MaxLlmCalls of 2 should win.
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, maxLlmCalls: 2);

        var responses = Enumerable.Range(0, 2)
            .Select(_ => (IReadOnlyList<AiChatChunk>)[new AiChatChunk("", [new AiToolCall("call1", "test_tool", "{}")], true)])
            .ToArray();
        var ollama = new FakeAIProvider(responses);
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("still going"))]);
        var service = CreateService(db, ollama, tools, new AgentSettings { MaxIterations = 25 });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None))
            events.Add(e);

        ollama.RecordedRequests.Should().HaveCount(2); // stopped at the session's own cap, not the global 25
        events.Should().ContainSingle(e => e is ErrorEvent && ((ErrorEvent)e).Message.Contains("Stopped after 2"));
    }

    [Fact]
    public async Task RunTurnAsync_EachLlmCall_StreamsTheSessionsNewTotal()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "test_tool", "{}")], true)],
            [new AiChatChunk("Done", null, true)]);
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("ok"))]);
        var service = CreateService(db, ollama, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None))
            events.Add(e);

        events.OfType<LlmCallEvent>().Select(e => e.TotalCalls).Should().Equal(1, 2);
    }

    [Fact]
    public async Task RunTurnAsync_ProviderReportsUsage_AccumulatesSessionTotalsAndStreamsThem()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "test_tool", "{}")], true, new AiTokenUsage(10, 5))],
            [new AiChatChunk("Done", null, true, new AiTokenUsage(20, 7))]);
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("ok"))]);
        var service = CreateService(db, ollama, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None))
            events.Add(e);

        events.OfType<TokenUsageEvent>().Should().Equal(new TokenUsageEvent(10, 5), new TokenUsageEvent(30, 12));
        var reloaded = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        reloaded!.TotalInputTokens.Should().Be(30);
        reloaded.TotalOutputTokens.Should().Be(12);
    }

    [Fact]
    public async Task RunTurnAsync_ProviderReportsNoUsage_EmitsNoTokenUsageEvent()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider([new AiChatChunk("Hi there", null, true)]);
        var service = CreateService(db, ollama, new ToolRegistry([]));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None))
            events.Add(e);

        events.OfType<TokenUsageEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task RunTurnAsync_EachCall_RelaysIncreasingCallBudgetToTheModel()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, maxLlmCalls: 5);

        var ollama = new FakeAIProvider([new AiChatChunk("Hi there", null, true)]);
        var service = CreateService(db, ollama, new ToolRegistry([]));

        await foreach (var _ in service.RunTurnAsync(session.Id, "Hi", CancellationToken.None)) { }

        var systemMessage = ollama.RecordedRequests[0][0];
        systemMessage.Role.Should().Be("system");
        systemMessage.Content.Should().Contain("This is LLM call 1 of 5");
    }

    [Fact]
    public async Task RunTurnAsync_EnabledSkill_IsAdvertisedAndInvokedAsToolCall()
    {
        await using var db = NewContext();
        var skill = await new SkillsRepository(db).CreateAsync(
            "Weather digest", "Summarizes the weather", "prompt", "ollama", "m", null, [], [], CancellationToken.None);
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, enabledSkillIds: [skill.Id]);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "skill_weather_digest", """{"input":"today"}""")], true)],
            [new AiChatChunk("It is sunny.", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => [new TextDeltaEvent("Sunny, 24C."), new DoneEvent()]);
        var service = CreateService(db, ollama, new ToolRegistry([]), skillExecution: skillExecution);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Weather?", CancellationToken.None))
            events.Add(e);

        ollama.RecordedToolSets[0].Should().ContainSingle(t => t.Name == "skill_weather_digest");
        skillExecution.Calls.Should().ContainSingle(c => c.SkillId == skill.Id && c.Input == "today");
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => !e.IsError && e.ResultContent == "Sunny, 24C.");
        events.Should().ContainSingle(e => e is DoneEvent);
    }

    [Fact]
    public async Task RunTurnAsync_SkillNotEnabledForSession_IsNotAdvertisedOrCallable()
    {
        await using var db = NewContext();
        var skill = await new SkillsRepository(db).CreateAsync(
            "Weather digest", null, "prompt", "ollama", "m", null, [], [], CancellationToken.None);
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "skill_weather_digest", "{}")], true)],
            [new AiChatChunk("ok", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => [new TextDeltaEvent("x"), new DoneEvent()]);
        var service = CreateService(db, ollama, new ToolRegistry([]), skillExecution: skillExecution);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "Weather?", CancellationToken.None))
            events.Add(e);

        ollama.RecordedToolSets[0].Should().NotContain(t => t.Name == "skill_weather_digest");
        skillExecution.Calls.Should().BeEmpty();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError);
    }

    [Fact]
    public async Task RunTurnAsync_TrimHistoryEnabled_TruncatesOversizedToolResult()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "test_tool", "{}")], true)],
            [new AiChatChunk("Done!", null, true)]);

        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted(new string('x', 100)))]);
        var agentSettings = new AgentSettings { MaxIterations = 25, ToolResultMaxChars = 10 };
        var service = CreateService(db, ollama, tools, agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "Search something", CancellationToken.None)) { }

        var toolMessage = ollama.RecordedRequests[1].Single(m => m.Role == "tool");
        toolMessage.Content.Should().StartWith(new string('x', 10));
        toolMessage.Content.Should().Contain("truncated");
        toolMessage.Content!.Length.Should().BeLessThan(100);
    }

    [Fact]
    public async Task RunTurnAsync_TrimHistoryEnabled_CompactsOldMessagesBeyondKeepWindow()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        for (var i = 0; i < 6; i++)
        {
            await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
            {
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = $"old message {i}"
            }, CancellationToken.None);
        }

        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary: user discussed old messages 0 through 4.", null, true)],
            [new AiChatChunk("Hi there", null, true)]);
        var agentSettings = new AgentSettings { MaxIterations = 25, HistoryKeepRecentMessages = 2, CompactionBatchSize = 2 };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "latest message", CancellationToken.None)) { }

        ollama.RecordedRequests.Should().HaveCount(2); // [0] compaction call, [1] main turn call
        var mainCall = ollama.RecordedRequests[1];
        mainCall.Should().ContainSingle(m => m.Role == "system" && m.Content!.Contains("Summary: user discussed old messages"));
        mainCall.Should().NotContain(m => m.Content == "old message 0");
        mainCall.Should().NotContain(m => m.Content == "old message 4");
        mainCall.Should().ContainSingle(m => m.Content == "old message 5");
        mainCall.Should().ContainSingle(m => m.Content == "latest message");

        var updated = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        updated!.HistorySummary.Should().Contain("Summary: user discussed old messages");
        updated.SummarizedThroughSequenceNumber.Should().Be(5); // folded seq 1-5 ("old message 0".."old message 4")
    }

    [Fact]
    public async Task RunTurnAsync_TrimHistoryEnabled_CompactionFoldsIncrementallyAndThreadsPriorSummary()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        for (var i = 0; i < 4; i++)
        {
            await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
            {
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = $"old message {i}"
            }, CancellationToken.None);
        }

        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary v1: covers old0-2.", null, true)],
            [new AiChatChunk("reply1", null, true)],
            [new AiChatChunk("Summary v2: covers old0-3 and turn1.", null, true)],
            [new AiChatChunk("reply2", null, true)]);
        var agentSettings = new AgentSettings { MaxIterations = 25, HistoryKeepRecentMessages = 2, CompactionBatchSize = 2 };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "turn1 msg", CancellationToken.None)) { }
        var afterTurn1 = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        var throughSeqAfterTurn1 = afterTurn1!.SummarizedThroughSequenceNumber; // snapshot the value — afterTurn1 is EF-change-tracked and will mutate once turn2's compaction saves
        throughSeqAfterTurn1.Should().Be(3); // folded "old message 0".."old message 2" (seq 1-3)
        afterTurn1.HistorySummary.Should().Be("Summary v1: covers old0-2.");

        await foreach (var _ in service.RunTurnAsync(session.Id, "turn2 msg", CancellationToken.None)) { }

        ollama.RecordedRequests.Should().HaveCount(4); // [0] compaction1, [1] main1, [2] compaction2, [3] main2
        var compaction2Call = ollama.RecordedRequests[2];
        compaction2Call.Should().ContainSingle(m =>
            m.Role == "user" && m.Content!.Contains("Previous summary:") && m.Content.Contains("Summary v1: covers old0-2."));

        var afterTurn2 = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        afterTurn2!.HistorySummary.Should().Be("Summary v2: covers old0-3 and turn1.");
        afterTurn2.SummarizedThroughSequenceNumber.Should().Be(5);
        afterTurn2.SummarizedThroughSequenceNumber.Should().BeGreaterThan(throughSeqAfterTurn1);
    }

    [Fact]
    public async Task RunTurnAsync_TrimHistoryEnabled_CompactionCharBudgetLimitsMessagesFoldedPerCall()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        for (var i = 0; i < 10; i++)
        {
            await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
            {
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = new string('a', 100)
            }, CancellationToken.None);
        }

        var ollama = new FakeAIProvider(
            [new AiChatChunk("sum1", null, true)],
            [new AiChatChunk("reply1", null, true)],
            [new AiChatChunk("sum2", null, true)],
            [new AiChatChunk("reply2", null, true)]);
        // CompactionBatchSize kept high so only the char trigger can fire; CompactionMaxCharsPerCall
        // (250) is small enough that only ~2 of the ~100-char pending messages fit per compaction pass.
        var agentSettings = new AgentSettings { MaxIterations = 25, HistoryKeepRecentMessages = 2, CompactionBatchSize = 100, CompactionMaxCharsPerCall = 250 };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "latest msg", CancellationToken.None)) { }
        var afterTurn1 = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        var throughSeqAfterTurn1 = afterTurn1!.SummarizedThroughSequenceNumber; // snapshot — afterTurn1 is EF-change-tracked and will mutate once turn2's compaction saves
        throughSeqAfterTurn1.Should().Be(2); // only 2 of the 9 pending messages fit under the 250-char budget

        await foreach (var _ in service.RunTurnAsync(session.Id, "second latest", CancellationToken.None)) { }
        var afterTurn2 = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        afterTurn2!.SummarizedThroughSequenceNumber.Should().Be(4); // catches up incrementally, still short of the full backlog
        afterTurn2.SummarizedThroughSequenceNumber.Should().BeGreaterThan(throughSeqAfterTurn1);
    }

    [Fact]
    public async Task RunTurnAsync_TrimHistoryEnabled_CharTriggerFiresBeforeCountTrigger()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        // Simulates a handful of large sandbox-style tool outputs: few messages, but already over the
        // char budget well before CompactionBatchSize's message count would ever be reached.
        for (var i = 0; i < 5; i++)
        {
            await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
            {
                SessionId = session.Id,
                Role = MessageRole.Tool,
                ToolName = "sandbox",
                Content = new string('y', 4000)
            }, CancellationToken.None);
        }

        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary of sandbox output.", null, true)],
            [new AiChatChunk("Hi there", null, true)]);
        var agentSettings = new AgentSettings { MaxIterations = 25, HistoryKeepRecentMessages = 1, CompactionBatchSize = 50, CompactionMaxCharsPerCall = 20000 };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "latest message", CancellationToken.None)) { }

        // 5 messages is far below CompactionBatchSize (50), but 5 * 4000 = 20000 chars reaches
        // CompactionMaxCharsPerCall — the char trigger alone must have fired the compaction call.
        ollama.RecordedRequests.Should().HaveCount(2);
        var updated = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        updated!.HistorySummary.Should().Be("Summary of sandbox output.");
        updated.SummarizedThroughSequenceNumber.Should().Be(5);
    }

    [Fact]
    public async Task RunTurnAsync_TrimHistoryEnabled_CompactionFailureFallsBackToDropWithNote()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        for (var i = 0; i < 6; i++)
        {
            await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
            {
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = $"old message {i}"
            }, CancellationToken.None);
        }

        var ollama = new FakeAIProvider([new AiChatChunk("Hi there", null, true)])
        {
            ThrowOnNextCall = new InvalidOperationException("provider unavailable")
        };
        var agentSettings = new AgentSettings { MaxIterations = 25, HistoryKeepRecentMessages = 2, CompactionBatchSize = 2 };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunTurnAsync(session.Id, "latest message", CancellationToken.None))
            events.Add(e);

        events.Should().ContainSingle(e => e is DoneEvent); // the turn still completes despite the failed compaction call
        ollama.RecordedRequests.Should().HaveCount(2); // [0] failed compaction attempt, [1] main call (legacy fallback)
        var mainCall = ollama.RecordedRequests[1];
        mainCall.Should().ContainSingle(m => m.Role == "system" && m.Content!.Contains("omitted"));
        mainCall.Should().NotContain(m => m.Content == "old message 0");
        mainCall.Should().ContainSingle(m => m.Content == "latest message");

        var updated = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        updated!.HistorySummary.Should().BeNull();
        updated.SummarizedThroughSequenceNumber.Should().Be(0);
    }

    [Fact]
    public async Task RunTurnAsync_CompactionCandidateEndsOnOrphanedToolMessage_ExtendsForwardInsteadOfDeferringForever()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        // The oldest pending message (seq 1) alone almost fills the char budget; its own tool result
        // (seq 2) can't also fit under the same budget. Before the fix, SafeFoldBoundary shrank the fold
        // back to 0 rather than orphan the tool result, deferring the pass forever — since "pending" is
        // rescanned from the same starting point every turn and nothing ages these two messages out, the
        // session would never compact again.
        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.Assistant,
            Content = new string('a', 90),
            ToolCallsJson = System.Text.Json.JsonSerializer.Serialize(new List<AiToolCall> { new("call1", "test_tool", "{}") })
        }, CancellationToken.None);
        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.Tool,
            ToolCallId = "call1",
            ToolName = "test_tool",
            Content = "ok"
        }, CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary: called test_tool.", null, true)],
            [new AiChatChunk("Hi there", null, true)]);
        var agentSettings = new AgentSettings
        {
            MaxIterations = 25, HistoryKeepRecentMessages = 1, CompactionBatchSize = 2, CompactionMaxCharsPerCall = 90
        };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "latest message", CancellationToken.None)) { }

        // Both messages folded together in one pass despite exceeding the 90-char budget — progress was
        // made instead of deferring forever.
        ollama.RecordedRequests.Should().HaveCount(2); // [0] compaction call, [1] main turn call
        var updated = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        updated!.SummarizedThroughSequenceNumber.Should().Be(2);
        updated.HistorySummary.Should().Be("Summary: called test_tool.");
    }

    [Fact]
    public async Task RunTurnAsync_CompactionFoldsAssistantToolCall_IncludesToolNameAndArgumentsInSummaryPrompt()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.User,
            Content = "please write notes.txt"
        }, CancellationToken.None);
        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.Assistant,
            Content = "", // models frequently emit no text alongside a pure tool call
            ToolCallsJson = System.Text.Json.JsonSerializer.Serialize(new List<AiToolCall>
            {
                new("call1", "write_file", """{"path":"notes.txt","content":"hi"}""")
            })
        }, CancellationToken.None);
        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.Tool,
            ToolCallId = "call1",
            ToolName = "write_file",
            Content = "Wrote 2 bytes to notes.txt"
        }, CancellationToken.None);
        await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
        {
            SessionId = session.Id,
            Role = MessageRole.User,
            Content = "thanks"
        }, CancellationToken.None);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary v1.", null, true)],
            [new AiChatChunk("You're welcome", null, true)]);
        var agentSettings = new AgentSettings
        {
            MaxIterations = 25, HistoryKeepRecentMessages = 1, CompactionBatchSize = 2
        };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "latest message", CancellationToken.None)) { }

        var compactionCall = ollama.RecordedRequests[0];
        // The tool call's name/arguments must survive into the summary prompt — not just the Assistant
        // message's raw (often blank) Content, which FormatForSummary used to send alone.
        compactionCall.Should().ContainSingle(m =>
            m.Role == "user" &&
            m.Content!.Contains("write_file(") &&
            m.Content.Contains("notes.txt"));
    }

    [Fact]
    public async Task RunTurnAsync_MultiIterationTurn_AttemptsCompactionAtMostOncePerTurn()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: true);

        for (var i = 0; i < 6; i++)
        {
            await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
            {
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = $"old message {i}"
            }, CancellationToken.None);
        }

        // Two tool-calling iterations before the model finally answers in plain text — each iteration
        // appends a new Tool-role message, which could keep re-crossing the compaction trigger if
        // compaction ran on every iteration instead of once per turn.
        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary v1.", null, true)], // compaction (iteration 1 only)
            [new AiChatChunk("", [new AiToolCall("call1", "test_tool", "{}")], true)], // main iteration 1
            [new AiChatChunk("Done", null, true)]); // main iteration 2 (no compaction call before it)
        var agentSettings = new AgentSettings { MaxIterations = 25, HistoryKeepRecentMessages = 2, CompactionBatchSize = 2 };
        var tools = new ToolRegistry([new FakeTool("test_tool", (_, _) => new ToolCompleted("ok"))]);
        var service = CreateService(db, ollama, tools, agentSettings);

        await foreach (var _ in service.RunTurnAsync(session.Id, "latest message", CancellationToken.None)) { }

        // Exactly one compaction call for the whole turn, regardless of how many LLM iterations it took.
        ollama.RecordedRequests.Should().HaveCount(3);
    }

    private static async Task AppendUserMessagesAsync(ChatSessionRepository repo, Guid sessionId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await repo.AppendMessageAsync(new PersonalAI.Core.Models.Entities.ChatMessage
            {
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = $"old message {i}"
            }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompactHistoryAsync_FoldsWholeHistoryIncludingKeptWindow_AndNextTurnSendsOnlySummaryPlusNewMessages()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        // Auto-compaction off: a manual summary must still be used.
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None, trimHistoryEnabled: false);
        await AppendUserMessagesAsync(repo, session.Id, 3);

        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary: three old messages.", null, true)],
            [new AiChatChunk("Hi there", null, true)]);
        var agentSettings = new AgentSettings { MaxIterations = 25, HistoryKeepRecentMessages = 40 };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.CompactHistoryAsync(session.Id, CancellationToken.None))
            events.Add(e);

        events.OfType<CompactionCompletedEvent>().Should().ContainSingle(e => e.FoldedMessages == 3);
        events.Should().ContainSingle(e => e is DoneEvent);

        var updated = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        updated!.HistorySummary.Should().Be("Summary: three old messages.");
        updated.SummarizedThroughSequenceNumber.Should().Be(3);

        var messages = await repo.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Should().HaveCount(4);
        messages[^1].Role.Should().Be(MessageRole.System);
        messages[^1].Content.Should().StartWith(ChatAgentService.CompactionMarkerPrefix);
        messages[^1].ExcludeFromContext.Should().BeTrue();

        await foreach (var _ in service.RunTurnAsync(session.Id, "latest message", CancellationToken.None)) { }

        var mainCall = ollama.RecordedRequests[1];
        mainCall.Should().ContainSingle(m => m.Role == "system" && m.Content!.Contains("Summary: three old messages."));
        mainCall.Should().NotContain(m => m.Content != null && m.Content.StartsWith("old message"));
        mainCall.Should().NotContain(m => m.Content != null && m.Content.StartsWith(ChatAgentService.CompactionMarkerPrefix));
        mainCall.Should().ContainSingle(m => m.Content == "latest message");
    }

    [Fact]
    public async Task CompactHistoryAsync_BacklogOverCharBudget_FoldsInSeveralPasses()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);
        await AppendUserMessagesAsync(repo, session.Id, 4); // 13 chars each

        var ollama = new FakeAIProvider(
            [new AiChatChunk("Summary v1.", null, true)],
            [new AiChatChunk("Summary v2.", null, true)]);
        var agentSettings = new AgentSettings { MaxIterations = 25, CompactionMaxCharsPerCall = 26 };
        var service = CreateService(db, ollama, new ToolRegistry([]), agentSettings);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.CompactHistoryAsync(session.Id, CancellationToken.None))
            events.Add(e);

        ollama.RecordedRequests.Should().HaveCount(2);
        ollama.RecordedRequests[1].Should().Contain(m => m.Content!.Contains("Summary v1."));
        events.OfType<CompactionCompletedEvent>().Should().ContainSingle(e => e.FoldedMessages == 4);

        var updated = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        updated!.HistorySummary.Should().Be("Summary v2.");
        updated.SummarizedThroughSequenceNumber.Should().Be(4);
    }

    [Fact]
    public async Task CompactHistoryAsync_NothingNewSinceLastCompaction_IsNoOpWithoutProviderCall()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);
        await AppendUserMessagesAsync(repo, session.Id, 2);

        var ollama = new FakeAIProvider([new AiChatChunk("Summary.", null, true)]);
        var service = CreateService(db, ollama, new ToolRegistry([]));

        await foreach (var _ in service.CompactHistoryAsync(session.Id, CancellationToken.None)) { }

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.CompactHistoryAsync(session.Id, CancellationToken.None))
            events.Add(e);

        ollama.RecordedRequests.Should().HaveCount(1); // only the first compaction called the provider
        events.OfType<CompactionCompletedEvent>().Should().ContainSingle(e => e.FoldedMessages == 0);
        (await repo.GetMessagesAsync(session.Id, CancellationToken.None))
            .Count(m => m.Role == MessageRole.System).Should().Be(1); // no second marker
    }

    [Fact]
    public async Task CompactHistoryAsync_ProviderFails_ThrowsAndLeavesSummaryUntouched()
    {
        await using var db = NewContext();
        var repo = new ChatSessionRepository(db);
        var session = await repo.CreateSessionAsync(null, "llama3.1:8b", CancellationToken.None);
        await AppendUserMessagesAsync(repo, session.Id, 2);

        var ollama = new FakeAIProvider([new AiChatChunk("unused", null, true)])
        {
            ThrowOnNextCall = new InvalidOperationException("provider unavailable")
        };
        var service = CreateService(db, ollama, new ToolRegistry([]));

        var act = async () => { await foreach (var _ in service.CompactHistoryAsync(session.Id, CancellationToken.None)) { } };
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Compaction failed*");

        var updated = await repo.GetSessionAsync(session.Id, CancellationToken.None);
        updated!.HistorySummary.Should().BeNull();
        updated.SummarizedThroughSequenceNumber.Should().Be(0);
        (await repo.GetMessagesAsync(session.Id, CancellationToken.None)).Should().HaveCount(2); // no marker
    }
}
