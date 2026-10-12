using System.Threading.Channels;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Sandbox;

/// <summary>IFrameTransport backed by in-memory channels — lets SandboxFramePump's correlation logic be
/// unit-tested with no real socket. <see cref="OnSend"/> lets a test play "the other end of the wire":
/// it runs (and is awaited) inline with every SendAsync call, so a test can synchronously push a
/// correlated response onto the inbound channel before SendAsync returns.</summary>
public sealed class FakeFrameTransport : IFrameTransport
{
    private readonly Channel<SandboxFrame> _inbound = Channel.CreateUnbounded<SandboxFrame>();
    private readonly object _sentLock = new();

    public List<SandboxFrame> Sent { get; } = [];

    public Func<SandboxFrame, Task>? OnSend { get; set; }

    public async Task SendAsync(SandboxFrame frame, CancellationToken ct)
    {
        lock (_sentLock) Sent.Add(frame);
        if (OnSend is not null)
            await OnSend(frame);
    }

    public async Task<SandboxFrame?> ReceiveAsync(CancellationToken ct)
    {
        if (await _inbound.Reader.WaitToReadAsync(ct) && _inbound.Reader.TryRead(out var frame))
            return frame;

        return null; // channel completed with nothing left to read — a graceful close
    }

    /// <summary>Enqueues a frame as if it arrived from the peer.</summary>
    public void Push(SandboxFrame frame) => _inbound.Writer.TryWrite(frame);

    /// <summary>Simulates the transport dying (broker crash, dropped socket, heartbeat timeout) — the
    /// next ReceiveAsync call returns null, which SandboxFramePump treats as a dead transport.</summary>
    public void Complete() => _inbound.Writer.TryComplete();
}
