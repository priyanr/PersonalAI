using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.Sandbox;

/// <summary>Fires the sandbox tool's optional HostStartUrl/HostStopUrl settings — plain webhook triggers
/// a user points at whatever wakes or sleeps the physical/virtual machine that runs the Docker host,
/// since the broker obviously can't dial in while that machine is powered off. Neither URL is required;
/// with both unset this is a no-op on both paths, matching the tool's behaviour before this existed.
///
/// Singleton (registered as such deliberately) because the debounce state below is exactly the kind of
/// thing that must NOT reset every time a scope is created — a scoped instance would defeat the point.</summary>
public interface ISandboxHostPowerSignaler
{
    /// <summary>Called by SandboxTool when it finds no broker connected. Returns a short, model-readable
    /// status line to fold into the tool's own "not connected" message — including when nothing happened
    /// because no URL is configured, or the cooldown is still active — and never throws.</summary>
    Task<string?> TryFireStartAsync(bool force, CancellationToken ct);

    /// <summary>Called by SandboxReaperHostedService's tick whenever it observes at least one
    /// still-active (not yet Destroyed) sandbox — resets the once-per-idle-period debounce on
    /// TryFireStopAsync, so the NEXT time everything goes idle, the stop signal fires again.</summary>
    void NotifyActiveSandboxObserved();

    /// <summary>Called by SandboxReaperHostedService's tick once it observes zero active sandboxes
    /// remaining. Fires at most once per idle period; a no-op if HostStopUrl isn't configured.</summary>
    Task TryFireStopAsync(CancellationToken ct);
}

public sealed class SandboxHostPowerSignaler(
    IHttpClientFactory httpClientFactory,
    ToolConfigCache configCache,
    TimeProvider timeProvider,
    IOptions<SandboxSettings> settings,
    ILogger<SandboxHostPowerSignaler> logger) : ISandboxHostPowerSignaler
{
    public const string HttpClientName = "sandbox-host-power";

    private readonly object _gate = new();
    private DateTimeOffset? _lastStartFiredAt;
    private bool _stopSignalSentForCurrentIdlePeriod;

    public async Task<string?> TryFireStartAsync(bool force, CancellationToken ct)
    {
        var url = configCache.GetValue("sandbox", "HostStartUrl");
        if (string.IsNullOrWhiteSpace(url))
            return null;

        lock (_gate)
        {
            var cooldown = TimeSpan.FromSeconds(Math.Max(1, settings.Value.HostStartCooldownSeconds));
            if (!force && _lastStartFiredAt is { } last && timeProvider.GetUtcNow() - last < cooldown)
                return "A start signal was already sent recently — give the host a little longer before retrying.";
            _lastStartFiredAt = timeProvider.GetUtcNow();
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Sandbox host start URL returned HTTP {Status}.", (int)response.StatusCode);
                return $"A start signal was sent, but the endpoint responded with HTTP {(int)response.StatusCode}.";
            }
            return "A start signal was sent to the configured Host Start URL — try again in a short while once it has booted.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to call the sandbox host start URL.");
            return $"Tried to send a start signal, but the request failed: {ex.Message}";
        }
    }

    public void NotifyActiveSandboxObserved()
    {
        lock (_gate) { _stopSignalSentForCurrentIdlePeriod = false; }
    }

    public async Task TryFireStopAsync(CancellationToken ct)
    {
        var url = configCache.GetValue("sandbox", "HostStopUrl");
        if (string.IsNullOrWhiteSpace(url))
            return;

        lock (_gate)
        {
            if (_stopSignalSentForCurrentIdlePeriod) return;
            _stopSignalSentForCurrentIdlePeriod = true;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Sandbox host stop URL returned HTTP {Status}.", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to call the sandbox host stop URL.");
        }
    }
}
