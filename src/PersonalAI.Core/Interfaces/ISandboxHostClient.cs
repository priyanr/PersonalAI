using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Core.Interfaces;

/// <summary>Result of one `exec` call, already demultiplexed and capped — the seam SandboxTool (a later
/// phase) depends on instead of the wire types, so a future Redis-relay implementation of
/// ISandboxHostClient (needed only if the API ever scales past one replica) can slot in without touching
/// the tool.</summary>
public sealed record SandboxExecResult(int? ExitCode, bool TimedOut, string Stdout, string Stderr,
                                       bool StdoutTruncated, bool StderrTruncated,
                                       string? Error = null, bool ContainerMissing = false);

/// <summary>Result of ensuring a sandbox exists. Ports are host==container, TCP — a server the model
/// starts inside the container must bind to one of these to be reachable from outside it.</summary>
public sealed record SandboxEnsureResult(string HostPath, IReadOnlyList<int> Ports);

/// <summary>What a caller inside the API needs from the sandbox host broker, independent of the WebSocket
/// underneath it. Implemented by SandboxHostRegistry, the singleton that owns the one live connection.</summary>
public interface ISandboxHostClient
{
    bool IsConnected { get; }

    /// <summary>The source IP address of the currently-connected broker's WebSocket, as the API's own TCP
    /// stack (or a trusted reverse proxy's X-Forwarded-For, per Program.cs's UseForwardedHeaders) sees it
    /// — null when no broker is connected. This is the address a published port is actually reachable at
    /// from outside the sandbox container, so SandboxTool's "ports" action uses it to give the model a
    /// real http://host:port URL instead of generic "reachable from outside" guidance.</summary>
    string? RemoteIp { get; }

    /// <param name="preferredPorts">Ports to try to reuse from a previous create of this same sandbox —
    /// empty on first creation. Best-effort: the broker falls back to fresh ports for any that are no
    /// longer free.</param>
    Task<SandboxEnsureResult> EnsureSandboxAsync(Guid sandboxId, SandboxKind kind, Guid ownerId, string image, IReadOnlyList<int> preferredPorts, CancellationToken ct);

    Task<SandboxExecResult> ExecAsync(Guid sandboxId, IReadOnlyList<string> command, string? workingDir, TimeSpan timeout, CancellationToken ct);

    Task<FileOpResponse> FileOpAsync(FileOpRequest request, CancellationToken ct);

    Task DestroySandboxAsync(Guid sandboxId, bool deleteFiles, CancellationToken ct);
}
