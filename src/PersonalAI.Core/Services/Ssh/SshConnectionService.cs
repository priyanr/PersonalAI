using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;

namespace PersonalAI.Core.Services.Ssh;

/// <summary>Runs a harmless probe over SSH, records the outcome on the SshServer, and pins the host key on
/// first success.</summary>
public sealed class SshConnectionService(ISshServersRepository repo, ISshRunner ssh)
{
    // Fixed template; never built from user or model input.
    private const string ProbeCommand = "uname -sr";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Returns null if the server doesn't exist.</summary>
    public async Task<SshServerTestResultDto?> TestAsync(Guid serverId, CancellationToken ct)
    {
        var server = await repo.GetServerAsync(serverId, ct);
        if (server is null) return null;

        var result = await ssh.RunAsync(server, ProbeCommand, ProbeTimeout, ct);
        var ok = result.Connected && result.Error is null && result.ExitCode == 0;
        var error = ok ? null : result.Error ?? $"Probe exited with code {result.ExitCode}: {result.Stderr.Trim()}";

        // Only pin when we actually connected and authenticated, never from a failed attempt.
        await repo.RecordConnectionAsync(serverId, ok, error, ok ? result.HostKeyFingerprint : null, ct);
        return new SshServerTestResultDto(ok, ok ? result.Stdout.Trim() : null, error, result.HostKeyFingerprint);
    }
}
