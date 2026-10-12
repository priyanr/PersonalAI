namespace PersonalAI.Sandbox.Protocol;

/// <summary>The seam SandboxFramePump runs over. A real WebSocketFrameTransport wraps a live socket;
/// tests provide a FakeFrameTransport backed by in-memory channels — this is what makes the correlation
/// layer unit-testable with no socket at all.</summary>
public interface IFrameTransport
{
    Task SendAsync(SandboxFrame frame, CancellationToken ct);

    /// <summary>Returns null when the peer closed the connection gracefully (as opposed to throwing,
    /// which signals an abnormal failure).</summary>
    Task<SandboxFrame?> ReceiveAsync(CancellationToken ct);
}
