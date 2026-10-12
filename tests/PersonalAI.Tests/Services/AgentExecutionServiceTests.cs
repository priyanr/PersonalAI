using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.Agents;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using PersonalAI.Tests.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace PersonalAI.Tests.Services;

public class AgentExecutionServiceTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AgentExecutionService CreateService(
        AppDbContext db, IAIProvider provider, ISkillExecutionService skillExecution,
        AgentSettings? agentSettings = null, ToolRegistry? tools = null, ISandboxHostClient? sandboxHostClient = null) => new(
        new AgentsRepository(db),
        new AgentRunsRepository(db),
        new SkillsRepository(db),
        skillExecution,
        new AIProviderRegistry([provider]),
        tools ?? new ToolRegistry([]),
        Options.Create(agentSettings ?? new AgentSettings { MaxIterations = 25 }),
        sandboxHostClient ?? Mock.Of<ISandboxHostClient>(),
        Mock.Of<ILogger<AgentExecutionService>>());

    [Fact]
    public async Task RunAsync_PlainTextReply_EndsWithDoneAndRecordsSucceededRun()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "You are helpful.", "ollama", "llama3.1:8b", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider([new AiChatChunk("All done", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var service = CreateService(db, provider, skillExecution);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        events.Should().ContainSingle(e => e is AgentRunStartedEvent);
        events.OfType<TextDeltaEvent>().Select(e => e.Text).Should().Equal("All done");
        events.Should().ContainSingle(e => e is DoneEvent);

        var runId = events.OfType<AgentRunStartedEvent>().Single().RunId;
        var runsRepo = new AgentRunsRepository(db);
        var run = await runsRepo.GetAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(AgentRunStatus.Succeeded);
        run.Summary.Should().Be("All done");
        run.Trigger.Should().Be(AgentRunTrigger.Manual);
    }

    [Fact]
    public async Task RunAsync_ToolCallForEnabledSkill_InvokesSkillExecutionWithInputArg()
    {
        await using var db = NewContext();
        var skillsRepo = new SkillsRepository(db);
        var skill = await skillsRepo.CreateAsync(
            "Weather digest", "Summarizes the weather", "prompt", "ollama", "m", null, [], [], CancellationToken.None);

        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [skill.Id], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "skill_weather_digest", """{"input":"today"}""")], true)],
            [new AiChatChunk("Sunny.", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => [new TextDeltaEvent("It's sunny."), new DoneEvent()]);
        var service = CreateService(db, provider, skillExecution);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        skillExecution.Calls.Should().ContainSingle(c => c.SkillId == skill.Id && c.Input == "today");
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => !e.IsError && e.ResultContent == "It's sunny.");
        events.Should().ContainSingle(e => e is DoneEvent);
        provider.RecordedRequests.Should().HaveCount(2);

        // The provider was advertised a tool definition for the enabled skill.
        provider.RecordedToolSets[0].Should().ContainSingle(t => t.Name == "skill_weather_digest");
    }

    [Fact]
    public async Task RunAsync_MidRun_PersistsTranscriptProgressivelyRatherThanOnlyAtCompletion()
    {
        await using var db = NewContext();
        var skillsRepo = new SkillsRepository(db);
        var skill = await skillsRepo.CreateAsync(
            "Weather digest", "Summarizes the weather", "prompt", "ollama", "m", null, [], [], CancellationToken.None);

        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [skill.Id], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "skill_weather_digest", """{"input":"today"}""")], true)],
            [new AiChatChunk("Sunny.", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => [new TextDeltaEvent("It's sunny."), new DoneEvent()]);
        var service = CreateService(db, provider, skillExecution);
        var runsRepo = new AgentRunsRepository(db);

        Guid runId = default;
        var sawMidRunTranscript = false;
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
        {
            if (e is AgentRunStartedEvent started)
            {
                runId = started.RunId;
            }
            else if (e is ToolResultEvent && !sawMidRunTranscript)
            {
                // The run hasn't produced its final answer yet — this checks that the skill result
                // is already visible to any reader polling the run row, not just once it finishes.
                var midRun = await runsRepo.GetAsync(runId, CancellationToken.None);
                midRun!.Status.Should().Be(AgentRunStatus.Running);
                var midRunTranscript = JsonSerializer.Deserialize<List<AgentRunTranscriptEntry>>(midRun.TranscriptJson)!;
                midRunTranscript.Should().ContainSingle(entry => entry.Content == "It's sunny.");
                sawMidRunTranscript = true;
            }
        }

        sawMidRunTranscript.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ToolCallForNameNotAdvertised_RejectsWithoutInvokingAnySkill()
    {
        await using var db = NewContext();
        var skillsRepo = new SkillsRepository(db);
        var skill = await skillsRepo.CreateAsync("Weather", null, "p", "ollama", "m", null, [], [], CancellationToken.None);

        var agentsRepo = new AgentsRepository(db);
        // Agent has no enabled skills, even though "Weather" exists in the catalog.
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "skill_weather", "{}")], true)],
            [new AiChatChunk("Understood", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => [new DoneEvent()]);
        var service = CreateService(db, provider, skillExecution);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        skillExecution.Calls.Should().BeEmpty();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("not one of this agent's enabled skills or tools"));
        _ = skill; // only present to prove the skill's existence elsewhere doesn't make it callable here
    }

    [Fact]
    public async Task RunAsync_ToolCallForEnabledDirectTool_ExecutesToolDirectlyWithoutASkill()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], ["gmail"], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "gmail", """{"action":"list_messages"}""")], true)],
            [new AiChatChunk("Checked.", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var tools = new ToolRegistry([new FakeTool("gmail", (_, _) => new ToolCompleted("2 unread"))]);
        var service = CreateService(db, provider, skillExecution, tools: tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        skillExecution.Calls.Should().BeEmpty();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => !e.IsError && e.ResultContent == "2 unread");
        events.Should().ContainSingle(e => e is DoneEvent);
        provider.RecordedToolSets[0].Should().ContainSingle(t => t.Name == "gmail");
    }

    [Fact]
    public async Task RunAsync_ToolCallForToolNotEnabledOnAgent_RejectsWithoutExecutingIt()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        // "gmail" exists in the registry but isn't in this agent's EnabledTools.
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var executed = false;
        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "gmail", "{}")], true)],
            [new AiChatChunk("Understood", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var tools = new ToolRegistry([new FakeTool("gmail", (_, _) => { executed = true; return new ToolCompleted("should not run"); })]);
        var service = CreateService(db, provider, skillExecution, tools: tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        executed.Should().BeFalse();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("not one of this agent's enabled skills or tools"));
    }

    [Fact]
    public async Task RunAsync_DirectToolRequiresApproval_RejectsInsteadOfPausing()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], ["write_file"], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "write_file", "{}")], true)],
            [new AiChatChunk("Understood", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var tools = new ToolRegistry([
            new FakeTool("write_file", (_, _) => new ToolApprovalRequired("outside workspace", "/outside/path"))
        ]);
        var service = CreateService(db, provider, skillExecution, tools: tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("requires user approval"));
    }

    [Fact]
    public async Task RunAsync_ExceedsMaxIterations_EmitsErrorAndRecordsFailedRun()
    {
        await using var db = NewContext();
        var skillsRepo = new SkillsRepository(db);
        var skill = await skillsRepo.CreateAsync("Loop", null, "p", "ollama", "m", null, [], [], CancellationToken.None);

        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [skill.Id], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var responses = Enumerable.Range(0, 3)
            .Select(_ => (IReadOnlyList<AiChatChunk>)[new AiChatChunk("", [new AiToolCall("call1", "skill_loop", "{}")], true)])
            .ToArray();
        var provider = new FakeAIProvider(responses);
        var skillExecution = new FakeSkillExecutionService((_, _) => [new TextDeltaEvent("still going"), new DoneEvent()]);
        var service = CreateService(db, provider, skillExecution, new AgentSettings { MaxIterations = 3 });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        events.Should().ContainSingle(e => e is ErrorEvent);
        var runId = events.OfType<AgentRunStartedEvent>().Single().RunId;
        var run = await new AgentRunsRepository(db).GetAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(AgentRunStatus.Failed);
    }

    [Fact]
    public async Task RunAsync_AgentMaxLlmCallsSet_OverridesGlobalDefaultAndCapsAtThatMany()
    {
        await using var db = NewContext();
        var skillsRepo = new SkillsRepository(db);
        var skill = await skillsRepo.CreateAsync("Loop", null, "p", "ollama", "m", null, [], [], CancellationToken.None);

        var agentsRepo = new AgentsRepository(db);
        // Global default is 25, but this agent's own MaxLlmCalls of 2 should win.
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [skill.Id], [], 2,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var responses = Enumerable.Range(0, 2)
            .Select(_ => (IReadOnlyList<AiChatChunk>)[new AiChatChunk("", [new AiToolCall("call1", "skill_loop", "{}")], true)])
            .ToArray();
        var provider = new FakeAIProvider(responses);
        var skillExecution = new FakeSkillExecutionService((_, _) => [new TextDeltaEvent("still going"), new DoneEvent()]);
        var service = CreateService(db, provider, skillExecution, new AgentSettings { MaxIterations = 25 });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        provider.RecordedRequests.Should().HaveCount(2); // stopped at the agent's own cap, not the global 25
        events.Should().ContainSingle(e => e is ErrorEvent && ((ErrorEvent)e).Message.Contains("Stopped after 2"));
    }

    [Fact]
    public async Task RunAsync_EachCall_RelaysIncreasingCallBudgetToTheModel()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "You are helpful.", "ollama", "m", null, [], [], 5,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider([new AiChatChunk("All done", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var service = CreateService(db, provider, skillExecution);

        await foreach (var _ in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None)) { }

        var systemMessage = provider.RecordedRequests[0][0];
        systemMessage.Role.Should().Be("system");
        systemMessage.Content.Should().Contain("This is LLM call 1 of 5");
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_EmitsErrorAndRecordsFailedRunWithoutCrashing()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new ThrowingAIProvider();
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var service = CreateService(db, provider, skillExecution);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        events.Should().ContainSingle(e => e is ErrorEvent);
        var runId = events.OfType<AgentRunStartedEvent>().Single().RunId;
        var run = await new AgentRunsRepository(db).GetAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(AgentRunStatus.Failed);
    }

    [Fact]
    public async Task RunAsync_TwoSkillsWithCollidingSlugs_GetDistinctToolNames()
    {
        await using var db = NewContext();
        var skillsRepo = new SkillsRepository(db);
        var skillA = await skillsRepo.CreateAsync("Weather!!!", null, "p", "ollama", "m", null, [], [], CancellationToken.None);
        var skillB = await skillsRepo.CreateAsync("Weather???", null, "p", "ollama", "m", null, [], [], CancellationToken.None);

        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [skillA.Id, skillB.Id], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider([new AiChatChunk("done", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var service = CreateService(db, provider, skillExecution);

        await foreach (var _ in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None)) { }

        var toolNames = provider.RecordedToolSets[0].Select(t => t.Name).ToList();
        toolNames.Should().HaveCount(2);
        toolNames.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_AfterSuccess_RecomputesAgentNextRunAtForIntervalSchedule()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Interval, null, 30, null, true, CancellationToken.None);
        var originalNextRunAt = agent.NextRunAt;

        var provider = new FakeAIProvider([new AiChatChunk("done", null, true)]);
        var skillExecution = new FakeSkillExecutionService((_, _) => []);
        var service = CreateService(db, provider, skillExecution);

        await foreach (var _ in service.RunAsync(agent.Id, AgentRunTrigger.Scheduled, CancellationToken.None)) { }

        var fetched = await agentsRepo.GetAsync(agent.Id, CancellationToken.None);
        fetched!.LastRunAt.Should().NotBeNull();
        fetched.NextRunAt.Should().NotBeNull();
        fetched.NextRunAt.Should().NotBe(originalNextRunAt); // re-armed from the run's completion time
    }

    private sealed class FlakyAIProvider(int failures) : IAIProvider
    {
        public int Calls { get; private set; }
        public string Key => "ollama";
        public string DisplayName => "Flaky";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];

        public async IAsyncEnumerable<AiChatChunk> ChatStreamAsync(
            string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools,
            int? numCtx, bool? think, string? reasoningEffort, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (++Calls <= failures)
                throw new HttpRequestException("Error while copying content to a stream.", new IOException("Connection reset by peer"));
            yield return new AiChatChunk("recovered", null, true);
        }

        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task RunAsync_RecordsLlmCallCount_NotCountingNetworkRetries()
    {
        await using var db = NewContext();
        var agent = await new AgentsRepository(db).CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);
        var provider = new FlakyAIProvider(failures: 1);
        var service = CreateService(db, provider, new FakeSkillExecutionService((_, _) => []),
            new AgentSettings { ProviderRetryCount = 3, ProviderRetryDelaySeconds = 0 });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        var runId = events.OfType<AgentRunStartedEvent>().Single().RunId;
        (await new AgentRunsRepository(db).GetAsync(runId, CancellationToken.None))!.LlmCallCount.Should().Be(1);
        events.OfType<LlmCallEvent>().Select(e => e.TotalCalls).Should().Equal(1);
    }

    [Fact]
    public async Task RunAsync_ProviderReportsUsage_AccumulatesTokenTotalsOnRunAndEmitsEvents()
    {
        await using var db = NewContext();
        var skill = await new SkillsRepository(db).CreateAsync(
            "Weather digest", "Summarizes the weather", "prompt", "ollama", "m", null, [], [], CancellationToken.None);
        var agent = await new AgentsRepository(db).CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [skill.Id], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);
        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "skill_weather_digest", """{"input":"today"}""")], true, new AiTokenUsage(100, 20))],
            [new AiChatChunk("Sunny.", null, true, new AiTokenUsage(150, 30))]);
        var service = CreateService(db, provider, new FakeSkillExecutionService((_, _) => [new TextDeltaEvent("It's sunny."), new DoneEvent()]));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        var runId = events.OfType<AgentRunStartedEvent>().Single().RunId;
        var run = (await new AgentRunsRepository(db).GetAsync(runId, CancellationToken.None))!;
        run.TotalInputTokens.Should().Be(250);
        run.TotalOutputTokens.Should().Be(50);
        events.OfType<TokenUsageEvent>().Select(e => (e.TotalInputTokens, e.TotalOutputTokens))
            .Should().Equal((100L, 20L), (250L, 50L));
    }

    [Fact]
    public async Task RunAsync_TransientNetworkErrorOnFirstAttempt_RetriesAndSucceeds()
    {
        await using var db = NewContext();
        var agent = await new AgentsRepository(db).CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);
        var provider = new FlakyAIProvider(failures: 3);
        var service = CreateService(db, provider, new FakeSkillExecutionService((_, _) => []),
            new AgentSettings { ProviderRetryCount = 3, ProviderRetryDelaySeconds = 0 });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        provider.Calls.Should().Be(4);
        events.Should().NotContain(e => e is ErrorEvent);
        var runId = events.OfType<AgentRunStartedEvent>().Single().RunId;
        (await new AgentRunsRepository(db).GetAsync(runId, CancellationToken.None))!.Status.Should().Be(AgentRunStatus.Succeeded);
    }

    [Fact]
    public async Task RunAsync_TransientErrorPersists_FailsOnceRetriedWithFullErrorChain()
    {
        await using var db = NewContext();
        var agent = await new AgentsRepository(db).CreateAsync(
            "a", null, "orchestrate", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);
        var provider = new FlakyAIProvider(failures: 10);
        var service = CreateService(db, provider, new FakeSkillExecutionService((_, _) => []),
            new AgentSettings { ProviderRetryCount = 3, ProviderRetryDelaySeconds = 0 });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        provider.Calls.Should().Be(4);
        var runId = events.OfType<AgentRunStartedEvent>().Single().RunId;
        var run = await new AgentRunsRepository(db).GetAsync(runId, CancellationToken.None);
        run!.Status.Should().Be(AgentRunStatus.Failed);
        run.Summary.Should().Contain("HttpRequestException").And.Contain("Connection reset by peer");
    }

    private sealed class ThrowingAIProvider : IAIProvider
    {
        public string Key => "ollama";
        public string DisplayName => "Throwing";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];

        // Throws on enumeration (not on the synchronous call) so AgentExecutionService's per-chunk
        // try/catch around MoveNextAsync is what has to catch it — matching a real provider's HTTP failure.
        public async IAsyncEnumerable<AiChatChunk> ChatStreamAsync(
            string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools,
            int? numCtx, bool? think, string? reasoningEffort, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("Simulated provider failure.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task RunAsync_EmptyReplyAtOutputLimit_RecordsFailedRunWithTheReason()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "You are helpful.", "ollama", "llama3.1:8b", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider([new AiChatChunk(null, null, true, new AiTokenUsage(10, 8192), HitOutputLimit: true)]);
        var service = CreateService(db, provider, new FakeSkillExecutionService((_, _) => []));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        events.Should().NotContain(e => e is DoneEvent);
        events.OfType<ErrorEvent>().Should().ContainSingle();
        var run = await new AgentRunsRepository(db).GetAsync(events.OfType<AgentRunStartedEvent>().Single().RunId, CancellationToken.None);
        run!.Status.Should().Be(AgentRunStatus.Failed);
        run.Summary.Should().Contain("8192").And.Contain("Max output tokens");
    }

    [Fact]
    public async Task RunAsync_AnswerCutOffAtOutputLimit_RecordsFailedRun()
    {
        await using var db = NewContext();
        var agentsRepo = new AgentsRepository(db);
        var agent = await agentsRepo.CreateAsync(
            "a", null, "You are helpful.", "ollama", "llama3.1:8b", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None);

        var provider = new FakeAIProvider([new AiChatChunk("Partial report", null, true, HitOutputLimit: true)]);
        var service = CreateService(db, provider, new FakeSkillExecutionService((_, _) => []));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None))
            events.Add(e);

        var run = await new AgentRunsRepository(db).GetAsync(events.OfType<AgentRunStartedEvent>().Single().RunId, CancellationToken.None);
        run!.Status.Should().Be(AgentRunStatus.Failed);
        run.Summary.Should().Contain("cut off");
    }

    [Fact]
    public async Task RunAsync_PassesTheAgentsReasoningEffort()
    {
        await using var db = NewContext();
        var agent = await new AgentsRepository(db).CreateAsync(
            "a", null, "You are helpful.", "ollama", "gpt-oss:20b", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, CancellationToken.None, reasoningEffort: "low");

        var provider = new FakeAIProvider([new AiChatChunk("All done", null, true)]);
        var service = CreateService(db, provider, new FakeSkillExecutionService((_, _) => []));

        await foreach (var _ in service.RunAsync(agent.Id, AgentRunTrigger.Manual, CancellationToken.None)) { }

        provider.RecordedReasoningEffort.Should().Equal(["low"]);
    }
}
