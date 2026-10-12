using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PersonalAI.Sandbox.Protocol;

/// <summary>The correlation engine shared by both ends of the sandbox WebSocket — the API's
/// SandboxHostRegistry and the broker's BrokerConnection wrap the exact same pump around their own
/// IFrameTransport. It owns:
///   - a single writer task draining a Channel&lt;SandboxFrame&gt;, because WebSocket sends are not
///     thread-safe and this is the only path that calls transport.SendAsync;
///   - a single reader task that dispatches every inbound frame either to a pending request's
///     TaskCompletionSource or to a registered exec-chunk callback;
///   - request/response correlation keyed by SandboxFrame.RequestId.
/// When the transport dies, every pending request is faulted with SandboxHostDisconnectedException
/// instead of hanging forever.</summary>
public sealed class SandboxFramePump : IAsyncDisposable
{
    private readonly IFrameTransport _transport;
    private readonly Channel<SandboxFrame> _outbox;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SandboxFrame>> _pending = new();
    private readonly ConcurrentDictionary<Guid, Action<SandboxFrame>> _chunkHandlers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerLoop;
    private readonly Task _readerLoop;

    public SandboxFramePump(IFrameTransport transport)
    {
        _transport = transport;
        _outbox = Channel.CreateUnbounded<SandboxFrame>(new UnboundedChannelOptions { SingleReader = true });
        _writerLoop = Task.Run(() => RunWriterLoopAsync(_cts.Token));
        _readerLoop = Task.Run(() => RunReaderLoopAsync(_cts.Token));
    }

    /// <summary>Fire-and-forget send — used for frames with no correlated response (Heartbeat,
    /// streamed ExecOutputChunk on the broker side, etc). Throws SandboxHostDisconnectedException once the
    /// transport has died, rather than queueing a frame no writer will ever send.</summary>
    public async Task SendAsync(SandboxFrame frame, CancellationToken ct)
    {
        try
        {
            await _outbox.Writer.WriteAsync(frame, ct);
        }
        catch (ChannelClosedException ex)
        {
            throw new SandboxHostDisconnectedException("The sandbox transport is closed.", ex);
        }
    }

    /// <summary>Registers a callback for every frame that arrives sharing this RequestId before the
    /// terminal response completes it — the exec streaming case, where N ExecOutputChunk frames precede
    /// one ExecCompleted, all sharing the ExecRequest's RequestId. The handler also observes the terminal
    /// frame (so a caller can render "chunk arrived" the same way for all of them); it is removed
    /// automatically the moment that terminal frame is dispatched.</summary>
    public void RegisterChunkHandler(Guid requestId, Action<SandboxFrame> onFrame) =>
        _chunkHandlers[requestId] = onFrame;

    /// <summary>Invoked from the reader loop for every inbound frame that does not correlate to a pending
    /// <see cref="SendRequestAsync"/> call and is not consumed by a registered chunk handler — i.e. a
    /// frame the *peer* initiated rather than one responding to something we sent. This is what lets the
    /// responding side of the protocol (the broker, replying to CreateSandboxRequest/ExecRequest/
    /// FileOpRequest/DestroySandboxRequest/ListSandboxesRequest sent by the API) observe inbound requests
    /// at all: without it, Dispatch has nowhere to hand such a frame and it is silently dropped. Purely
    /// additive — a pump with no handler set behaves exactly as before this property existed. Runs
    /// synchronously on the reader loop, so a handler that does real work should offload it (e.g.
    /// Task.Run) rather than block subsequent reads.</summary>
    public Action<SandboxFrame>? OnRequest { get; set; }

    /// <summary>Completes once both the writer and reader loops have exited — i.e. the transport has
    /// died (gracefully or not) or the pump was disposed. Either loop exiting shuts the other down too
    /// (see <see cref="Shutdown"/>): without that, a dead socket ends the reader while the writer stays
    /// parked forever on an empty outbox, and this task never resolves. Both loops swallow their own exceptions
    /// internally (see below), so this task itself never faults; callers that need to notice "the
    /// connection is gone and it's time to reconnect" without having any request in flight (the broker's
    /// steady-state condition, where it is purely reacting to <see cref="OnRequest"/> and has nothing
    /// pending) await this instead of polling.</summary>
    public Task Completion => Task.WhenAll(_writerLoop, _readerLoop);

    /// <summary>Sends <paramref name="request"/> and awaits the frame that echoes its RequestId, subject
    /// to <paramref name="timeout"/>. Throws SandboxHostDisconnectedException if the transport dies while
    /// waiting, or TimeoutException/OperationCanceledException on timeout/cancellation.</summary>
    public async Task<SandboxFrame> SendRequestAsync(SandboxFrame request, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SandboxFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId, tcs))
            throw new InvalidOperationException($"A request with id {request.RequestId} is already pending.");

        try
        {
            await SendAsync(request, ct);
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
            _chunkHandlers.TryRemove(request.RequestId, out _);
        }
    }

    private async Task RunWriterLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync(ct))
            {
                await _transport.SendAsync(frame, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown via DisposeAsync.
        }
        catch (Exception ex)
        {
            // The transport failed on send; the reader loop (reading the same dead transport) will
            // observe the failure too and fault every pending request — nothing further to do here
            // beyond making sure we don't leave the writer loop crashing silently.
            FaultAllPending(new SandboxHostDisconnectedException("The sandbox transport failed while sending.", ex));
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task RunReaderLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _transport.ReceiveAsync(ct);
                if (frame is null)
                    break; // graceful close

                Dispatch(frame);
            }

            FaultAllPending(new SandboxHostDisconnectedException("The sandbox transport closed."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown via DisposeAsync — nothing pending should be left hanging, but fault
            // defensively in case something raced in just before cancellation.
            FaultAllPending(new SandboxHostDisconnectedException("The sandbox transport was disposed."));
        }
        catch (Exception ex)
        {
            FaultAllPending(new SandboxHostDisconnectedException("The sandbox transport failed while receiving.", ex));
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Stops both loops: completing the outbox wakes a writer parked on an empty channel (and
    /// makes any later <see cref="SendAsync"/> throw instead of queueing into a dead pump), and cancelling
    /// unblocks a reader or a writer stuck mid-call on the transport. Idempotent — called from each loop's
    /// exit and from DisposeAsync.</summary>
    private void Shutdown()
    {
        _outbox.Writer.TryComplete();
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed — both loops have exited, nothing left to stop.
        }
    }

    private void Dispatch(SandboxFrame frame)
    {
        if (_chunkHandlers.TryGetValue(frame.RequestId, out var handler))
        {
            handler(frame);

            // Anything that isn't a chunk (i.e. ExecCompleted) is terminal — the handler has now seen
            // it, so retire the handler before falling through to resolve the pending request below.
            if (frame is not ExecOutputChunk)
                _chunkHandlers.TryRemove(frame.RequestId, out _);
        }

        // An ExecOutputChunk is NEVER a response: N of them share the ExecRequest's RequestId before the
        // terminal ExecCompleted arrives. This check is deliberately outside the handler branch above —
        // if it lived inside it, a chunk that raced ahead of RegisterChunkHandler (or a caller that
        // forgot to register one at all) would resolve SendRequestAsync with a chunk instead of the
        // completion, and the exec would report the wrong frame type rather than waiting.
        if (frame is ExecOutputChunk)
            return;

        if (_pending.TryRemove(frame.RequestId, out var tcs))
        {
            tcs.TrySetResult(frame);
            return;
        }

        OnRequest?.Invoke(frame);
    }

    /// <summary>Faults every request currently awaiting a response. Exposed for tests and for callers
    /// that detect transport death out-of-band (e.g. a heartbeat timeout aborting the socket).</summary>
    public void FaultAllPending(Exception ex)
    {
        foreach (var requestId in _pending.Keys)
        {
            if (_pending.TryRemove(requestId, out var tcs))
                tcs.TrySetException(ex);
        }
        _chunkHandlers.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Shutdown();
        try
        {
            await Task.WhenAll(_writerLoop, _readerLoop);
        }
        catch
        {
            // Loop shutdown exceptions are expected (OperationCanceledException) and already handled
            // inside each loop; DisposeAsync itself must not throw.
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
