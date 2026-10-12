namespace PersonalAI.Sandbox.Protocol;

/// <summary>Thrown to fault every in-flight request when the underlying transport dies — a broker crash,
/// a dropped WebSocket, a heartbeat timeout. The command's actual effect on the sandbox is unknown at
/// that point (e.g. an exec may have completed or may still be running), so callers should render this as
/// "unknown outcome", not "failed".</summary>
public sealed class SandboxHostDisconnectedException : Exception
{
    public SandboxHostDisconnectedException()
        : base("The sandbox host disconnected.")
    {
    }

    public SandboxHostDisconnectedException(string message)
        : base(message)
    {
    }

    public SandboxHostDisconnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
