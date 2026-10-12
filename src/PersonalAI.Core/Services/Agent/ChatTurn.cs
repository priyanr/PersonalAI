using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace PersonalAI.Core.Services.Agent;

/// <summary>One chat turn running in the background under <see cref="ChatTurnRunner"/>, decoupled from any
/// HTTP request. Buffers every event it publishes so a subscriber that attaches late (a reloaded page, a
/// second tab) replays the turn so far and then continues live. Consecutive token events are coalesced
/// in the buffer so a long answer replays as one chunk rather than thousands.</summary>
public sealed class ChatTurn
{
    private readonly object _gate = new();
    private readonly List<AgentStreamEvent> _buffer = [];
    private readonly StringBuilder _pendingText = new();
    private readonly List<Channel<AgentStreamEvent>> _subscribers = [];
    private readonly CancellationTokenSource _cts = new();
    private bool _completed;
    private string? _stopReason;

    internal ChatTurn(Guid sessionId, DateTimeOffset startedAt)
    {
        SessionId = sessionId;
        StartedAt = startedAt;
    }

    public Guid SessionId { get; }
    public DateTimeOffset StartedAt { get; }

    internal CancellationToken Token => _cts.Token;
    internal string StopReason => _stopReason ?? "the turn was stopped";
    internal Task Completion { get; set; } = Task.CompletedTask;

    /// <summary>Requests cancellation. The first reason wins (a user stop isn't relabelled by a later
    /// shutdown). False once the turn has already completed.</summary>
    internal bool RequestStop(string reason)
    {
        lock (_gate)
        {
            if (_completed) return false;
            _stopReason ??= reason;
        }
        _cts.Cancel();
        return true;
    }

    internal void Publish(AgentStreamEvent evt)
    {
        lock (_gate)
        {
            if (_completed) return;
            // Reasoning is live-only: it can run to thousands of tokens and is never persisted, so a late
            // subscriber just doesn't see the thinking that happened before it attached.
            if (evt is TextDeltaEvent delta)
                _pendingText.Append(delta.Text);
            else if (evt is not ReasoningDeltaEvent)
            {
                FlushPendingText();
                _buffer.Add(evt);
            }
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(evt);
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            FlushPendingText();
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
        // _cts is deliberately never disposed: RequestStop may race with this and a timer-less CTS holds
        // nothing that needs releasing.
    }

    /// <summary>Replays everything published so far, then streams live until the turn completes.
    /// Cancelling <paramref name="ct"/> only detaches this subscriber — the turn keeps running.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions { SingleReader = true });
        List<AgentStreamEvent> replay;
        lock (_gate)
        {
            // Snapshot and subscribe under the same lock so no event falls between the replay and the live feed.
            replay = [.. _buffer];
            if (_pendingText.Length > 0) replay.Add(new TextDeltaEvent(_pendingText.ToString()));
            if (_completed) channel.Writer.TryComplete();
            else _subscribers.Add(channel);
        }

        try
        {
            foreach (var evt in replay)
                yield return evt;
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
        }
    }

    private void FlushPendingText()
    {
        if (_pendingText.Length == 0) return;
        _buffer.Add(new TextDeltaEvent(_pendingText.ToString()));
        _pendingText.Clear();
    }
}
