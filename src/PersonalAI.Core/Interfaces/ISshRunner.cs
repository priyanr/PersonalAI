using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

/// <summary>Outcome of one SSH command. <c>Connected</c> is false when the connection or authentication
/// failed (including a host-key mismatch); <c>Error</c> is then set. A command that ran but failed has
/// Connected = true and a non-zero ExitCode.</summary>
public record SshCommandResult(bool Connected, int ExitCode, string Stdout, string Stderr, string? Error, string? HostKeyFingerprint);

/// <summary>Runs one fixed command on a SshServer over SSH. Callers pass only command templates owned by
/// collector code, never model-supplied shell text. Enforces the server's pinned host key.</summary>
public interface ISshRunner
{
    Task<SshCommandResult> RunAsync(SshServer server, string command, TimeSpan timeout, CancellationToken ct);
}
