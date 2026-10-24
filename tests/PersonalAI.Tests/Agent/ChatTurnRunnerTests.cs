using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Sessions;
using PersonalAI.Core.Services.Settings;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Agent;

public class ChatTurnRunnerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Streams "partial" and then waits until cancelled, so a stopped turn has in-flight text to save.</summary>
    private sealed class StallingProvider : IAIProvider
    {
        public string Key => "ollama";
        public string DisplayName => "Stalling";
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];

        public async IAsyncEnumerable<AiChatChunk> ChatStreamAsync(
            string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools,
            int? numCtx, bool? think, string? reasoningEffort, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AiChatChunk("partial", null, false);
            await Task.Delay(Timeout.Infinite, ct);
        }

        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>A runner whose scopes get a real ChatAgentService on one shared in-memory database.</summary>
    private static (ChatTurnRunner Runner, Func<AppDbContext> NewDb) CreateRunner()
    {
        var dbName = Guid.NewGuid().ToString();
        AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);
        var services = new ServiceCollection()
            .AddScoped(_ => NewDb())
            .AddScoped(sp =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                return new ChatAgentService(
                    db, new ChatSessionRepository(db), new SkillsRepository(db), new FakeSkillProvider().CreateService(db),
                    new ChatHistoryCompaction(db, new AIProviderRegistry([new StallingProvider()]), Options.Create(new AgentSettings { MaxIterations = 25 })),
                    new AIProviderRegistry([new StallingProvider()]), new ToolRegistry([]),
                    Options.Create(new OllamaSettings { BaseUrl = "http://localhost:11434", DefaultSystemPrompt = "system" }),
                    Options.Create(new AgentSettings { MaxIterations = 25 }), new AppSettingsCache());
            })
            .BuildServiceProvider();
        return (new ChatTurnRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ChatTurnRunner>.Instance), NewDb);
    }

    private static async Task<Guid> CreateSessionAsync(Func<AppDbContext> newDb)
    {
        await using var db = newDb();
        return (await TestSeed.SessionAsync(db, null, "m", CancellationToken.None)).Id;
    }

    private static async Task<IReadOnlyList<ChatMessage>> MessagesAsync(Func<AppDbContext> newDb, Guid sessionId)
    {
        await using var db = newDb();
        return await new ChatSessionRepository(db).GetMessagesAsync(sessionId, CancellationToken.None);
    }

    /// <summary>A turn body the test drives by hand: writes to <paramref name="source"/> become events.</summary>
    private static async IAsyncEnumerable<AgentStreamEvent> Feed(ChannelReader<AgentStreamEvent> source, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in source.ReadAllAsync(ct))
            yield return evt;
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(ChatTurn turn, CancellationToken ct = default)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var e in turn.SubscribeAsync(ct)) events.Add(e);
        return events;
    }

    private static async Task WaitUntilGoneAsync(ChatTurnRunner runner, Guid sessionId)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (runner.GetActive(sessionId) is not null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    [Fact]
    public async Task LateSubscriber_ReplaysCoalescedTokens_ThenStreamsLive()
    {
        var (runner, _) = CreateRunner();
        var sessionId = Guid.NewGuid();
        var source = Channel.CreateUnbounded<AgentStreamEvent>();
        var turn = runner.TryStart(sessionId, (_, ct) => Feed(source.Reader, ct))!;

        // First viewer leaves mid-turn (a page reload) — that must not stop the turn.
        using (var firstViewer = new CancellationTokenSource())
        {
            await source.Writer.WriteAsync(new TurnStartedEvent(3));
            await source.Writer.WriteAsync(new TextDeltaEvent("Hel"));
            await source.Writer.WriteAsync(new TextDeltaEvent("lo"));
            await firstViewer.CancelAsync();
            await FluentActions.Awaiting(() => CollectAsync(turn, firstViewer.Token)).Should().ThrowAsync<OperationCanceledException>();
        }
        await Task.Delay(50);
        runner.GetActive(sessionId).Should().BeSameAs(turn);

        var lateViewer = CollectAsync(turn);
        await source.Writer.WriteAsync(new ToolProgressEvent(Guid.Empty, "sandbox", "booting"));
        await source.Writer.WriteAsync(new DoneEvent());
        source.Writer.Complete();

        var events = await lateViewer.WaitAsync(Wait);
        events.Should().Equal(
            new TurnStartedEvent(3),
            new TextDeltaEvent("Hello"),
            new ToolProgressEvent(Guid.Empty, "sandbox", "booting"),
            new DoneEvent());
    }

    [Fact]
    public async Task OneTurnPerSession_AndUnregisteredOnceComplete()
    {
        var (runner, _) = CreateRunner();
        var sessionId = Guid.NewGuid();
        var source = Channel.CreateUnbounded<AgentStreamEvent>();
        var turn = runner.TryStart(sessionId, (_, ct) => Feed(source.Reader, ct))!;

        runner.TryStart(sessionId, (_, ct) => Feed(source.Reader, ct)).Should().BeNull();
        runner.TryStart(Guid.NewGuid(), (_, ct) => Feed(Channel.CreateUnbounded<AgentStreamEvent>().Reader, ct)).Should().NotBeNull();

        var viewer = CollectAsync(turn);
        source.Writer.Complete();
        await viewer.WaitAsync(Wait);

        // Unregistered before subscribers are released, so a client refetching right as its stream ends
        // already sees "not running".
        runner.GetActive(sessionId).Should().BeNull();
        runner.TryStart(sessionId, (_, ct) => Feed(Channel.CreateUnbounded<AgentStreamEvent>().Reader, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task TryStop_SavesInFlightTextAndNotice_ThenPublishesDone()
    {
        var (runner, newDb) = CreateRunner();
        var sessionId = await CreateSessionAsync(newDb);
        var turn = runner.TryStart(sessionId, (agent, ct) => agent.RunTurnAsync(sessionId, "hi", ct))!;
        var sawText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewer = Task.Run(async () =>
        {
            var seen = new List<AgentStreamEvent>();
            await foreach (var e in turn.SubscribeAsync(CancellationToken.None))
            {
                seen.Add(e);
                if (e is TextDeltaEvent) sawText.TrySetResult();
            }
            return seen;
        });
        await sawText.Task.WaitAsync(Wait);

        runner.TryStop(sessionId).Should().BeTrue();

        var events = await viewer.WaitAsync(Wait);
        events[^1].Should().BeOfType<DoneEvent>();
        var messages = await MessagesAsync(newDb, sessionId);
        messages.Select(m => (m.Role, m.Content)).Should().Equal(
            (MessageRole.User, "hi"),
            (MessageRole.Assistant, "partial"),
            (MessageRole.System, $"Turn interrupted: {ChatTurnRunner.UserStopReason}."));
        await WaitUntilGoneAsync(runner, sessionId);
        runner.TryStop(sessionId).Should().BeFalse();
    }

    [Fact]
    public async Task FailingTurn_FinalizesWithoutNotice_AndPublishesError()
    {
        var (runner, newDb) = CreateRunner();
        var sessionId = await CreateSessionAsync(newDb);
        var turn = runner.TryStart(sessionId, (_, ct) => Throwing(ct))!;

        var events = await CollectAsync(turn).WaitAsync(Wait);

        events[^1].Should().Be(new ErrorEvent("provider exploded"));
        (await MessagesAsync(newDb, sessionId)).Should().NotContain(m => m.Role == MessageRole.System);

        static async IAsyncEnumerable<AgentStreamEvent> Throwing([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new TurnStartedEvent(1);
            throw new InvalidOperationException("provider exploded");
        }
    }

    [Fact]
    public async Task StopAllAsync_StopsEveryTurnWithTheShutdownReason()
    {
        var (runner, newDb) = CreateRunner();
        var a = await CreateSessionAsync(newDb);
        var b = await CreateSessionAsync(newDb);
        runner.TryStart(a, (_, ct) => Feed(Channel.CreateUnbounded<AgentStreamEvent>().Reader, ct));
        runner.TryStart(b, (_, ct) => Feed(Channel.CreateUnbounded<AgentStreamEvent>().Reader, ct));

        await runner.StopAllAsync(CancellationToken.None).WaitAsync(Wait);

        foreach (var sessionId in new[] { a, b })
            (await MessagesAsync(newDb, sessionId)).Should().ContainSingle(m =>
                m.Role == MessageRole.System && m.Content == $"Turn interrupted: {ChatTurnRunner.ShutdownStopReason}.");
        runner.GetActive(a).Should().BeNull();
        runner.GetActive(b).Should().BeNull();
    }
}
