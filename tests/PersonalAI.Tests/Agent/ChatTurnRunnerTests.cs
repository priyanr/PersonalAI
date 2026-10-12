using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Services.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace PersonalAI.Tests.Agent;

public class ChatTurnRunnerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Only the finalize hook and InFlightAssistantText matter here — each turn's body is supplied
    /// by the test itself, so the loop methods are never called.</summary>
    private sealed class RecordingAgent(FinalizeLog log) : IChatAgentService
    {
        public string? InFlightAssistantText => log.InFlightText;

        public Task FinalizeInterruptedTurnAsync(Guid sessionId, string? partialAssistantText, string interruptionReason, string? systemNotice, CancellationToken ct)
        {
            lock (log) log.Calls.Add((sessionId, partialAssistantText, interruptionReason, systemNotice));
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentStreamEvent> RunTurnAsync(Guid sessionId, string userText, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentStreamEvent> ContinueTurnAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentStreamEvent> ResolveApprovalAsync(Guid toolInvocationId, bool approved, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentStreamEvent> CompactHistoryAsync(Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FinalizeLog
    {
        public string? InFlightText { get; set; }
        public List<(Guid SessionId, string? Partial, string Reason, string? Notice)> Calls { get; } = [];
    }

    private static (ChatTurnRunner Runner, FinalizeLog Log) CreateRunner()
    {
        var log = new FinalizeLog();
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddScoped<IChatAgentService, RecordingAgent>()
            .BuildServiceProvider();
        return (new ChatTurnRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ChatTurnRunner>.Instance), log);
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
    public async Task TryStop_FinalizesWithPartialTextAndNotice_ThenPublishesDone()
    {
        var (runner, log) = CreateRunner();
        var sessionId = Guid.NewGuid();
        var source = Channel.CreateUnbounded<AgentStreamEvent>();
        var turn = runner.TryStart(sessionId, (_, ct) => Feed(source.Reader, ct))!;
        var viewer = CollectAsync(turn);
        await source.Writer.WriteAsync(new TextDeltaEvent("partial"));
        log.InFlightText = "partial";

        runner.TryStop(sessionId).Should().BeTrue();

        var events = await viewer.WaitAsync(Wait);
        events[^1].Should().BeOfType<DoneEvent>();
        log.Calls.Should().ContainSingle().Which.Should().Be(
            (sessionId, "partial", ChatTurnRunner.UserStopReason, $"Turn interrupted: {ChatTurnRunner.UserStopReason}."));
        await WaitUntilGoneAsync(runner, sessionId);
        runner.TryStop(sessionId).Should().BeFalse();
    }

    [Fact]
    public async Task FailingTurn_FinalizesWithoutNotice_AndPublishesError()
    {
        var (runner, log) = CreateRunner();
        var sessionId = Guid.NewGuid();
        var turn = runner.TryStart(sessionId, (_, ct) => Throwing(ct))!;

        var events = await CollectAsync(turn).WaitAsync(Wait);

        events[^1].Should().Be(new ErrorEvent("provider exploded"));
        log.Calls.Should().ContainSingle().Which.Notice.Should().BeNull();

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
        var (runner, log) = CreateRunner();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        runner.TryStart(a, (_, ct) => Feed(Channel.CreateUnbounded<AgentStreamEvent>().Reader, ct));
        runner.TryStart(b, (_, ct) => Feed(Channel.CreateUnbounded<AgentStreamEvent>().Reader, ct));

        await runner.StopAllAsync(CancellationToken.None).WaitAsync(Wait);

        log.Calls.Select(c => c.SessionId).Should().BeEquivalentTo([a, b]);
        log.Calls.Should().OnlyContain(c => c.Reason == ChatTurnRunner.ShutdownStopReason);
        runner.GetActive(a).Should().BeNull();
        runner.GetActive(b).Should().BeNull();
    }
}
