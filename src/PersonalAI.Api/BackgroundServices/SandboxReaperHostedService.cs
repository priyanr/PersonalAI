using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Sandbox.Protocol;
using Microsoft.Extensions.Options;

namespace PersonalAI.Api.BackgroundServices;

/// <summary>Polls for sandboxes that should be torn down and destroys them via ISandboxHostClient — the
/// safety net for the ephemeral teardown SkillExecutionService/AgentExecutionService already do in their
/// own finally blocks (this catches hard process kills, where finally never runs) and the only mechanism
/// that ever retires an idle chat-session container. Same shape as AgentSchedulerHostedService: one scope
/// per tick to select candidates, one scope per item so a single failure doesn't stop the batch or kill
/// the loop.
///
/// Also the trigger for the sandbox tool's HostStopUrl: once a tick's reap work leaves zero active (not
/// yet Destroyed) sandboxes, this is the signal that nothing needs the Docker host's machine anymore.</summary>
public sealed class SandboxReaperHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SandboxSettings> settings,
    ISandboxHostPowerSignaler powerSignaler,
    ToolConfigCache configCache,
    ILogger<SandboxReaperHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, settings.Value.ReapPollSeconds));
        using var timer = new PeriodicTimer(interval);

        while (true)
        {
            try
            {
                await ReapAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sandbox reaper tick failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReapAsync(CancellationToken ct)
    {
        var due = await SelectCandidatesAsync(ct);

        foreach (var (instance, deleteFiles) in due)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var hostClient = scope.ServiceProvider.GetRequiredService<ISandboxHostClient>();
                if (!hostClient.IsConnected)
                {
                    // Nothing reachable to destroy against this tick — leave the row as-is and retry
                    // next tick rather than marking it Destroyed for a container that may still exist.
                    continue;
                }

                await hostClient.DestroySandboxAsync(instance.Id, deleteFiles, ct);

                var repo = scope.ServiceProvider.GetRequiredService<ISandboxRepository>();
                instance.Status = SandboxInstanceStatus.Destroyed;
                instance.DestroyedAt = DateTimeOffset.UtcNow;
                await repo.UpdateAsync(instance, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to reap sandbox {SandboxId} (owner {OwnerId}, kind {Kind}).",
                    instance.Id, instance.OwnerId, instance.Kind);
            }
        }

        await SignalHostPowerStateAsync(ct);
    }

    /// <summary>Checked after this tick's reap work has applied, not before — a sandbox reaped THIS tick
    /// must not count as "still active" and block the stop signal. GetAllActiveAsync's "not yet Destroyed"
    /// filter deliberately still counts a Creating/Orphaned row: either represents ongoing intent to use
    /// a sandbox (an Orphaned one will simply be recreated on next use), so neither should trigger a
    /// premature stop. Public — the same reasoning SandboxHostController.NormalizeRemoteIp documents for
    /// its own visibility: this is the one piece of real decision logic in an otherwise scope/DB-coupled
    /// tick, worth testing directly rather than only indirectly through the full BackgroundService loop.</summary>
    public async Task SignalHostPowerStateAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISandboxRepository>();
        var stillActive = await repo.GetAllActiveAsync(ct);

        if (stillActive.Count > 0)
            powerSignaler.NotifyActiveSandboxObserved();
        else
            await powerSignaler.TryFireStopAsync(ct);
    }

    private async Task<IReadOnlyList<(SandboxInstance Instance, bool DeleteFiles)>> SelectCandidatesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISandboxRepository>();
        var sessions = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();

        var now = DateTimeOffset.UtcNow;
        var sessionCutoff = now - TimeSpan.FromMinutes(Math.Max(1, EffectiveIdleTimeoutMinutes()));
        var ephemeralCutoff = now - TimeSpan.FromMinutes(Math.Max(1, settings.Value.EphemeralGraceMinutes));

        var idleCandidates = await repo.GetIdleCandidatesAsync(sessionCutoff, ephemeralCutoff, ct);
        var allActive = await repo.GetAllActiveAsync(ct);
        var sessionSandboxes = allActive.Where(i => i.Kind == SandboxKind.Session).ToList();

        // Deduplicated by OwnerId — a chat session normally has at most one live sandbox. This uses the
        // existence-only query rather than GetSessionAsync, which would drag every message and tool
        // invocation of each conversation into memory on every poll just to answer "is it still there?".
        var existingSessionIds = new HashSet<Guid>();
        foreach (var ownerId in sessionSandboxes.Select(i => i.OwnerId).Distinct())
        {
            if (await sessions.SessionExistsAsync(ownerId, ct))
                existingSessionIds.Add(ownerId);
        }

        return SandboxReapPlanner.Plan(idleCandidates, sessionSandboxes, existingSessionIds);
    }

    /// <summary>The sandbox tool's "IdleTimeoutMinutes" setting (live, no restart) wins over
    /// Sandbox:IdleTimeoutMinutes; a blank or unparseable value falls back to the config.</summary>
    private int EffectiveIdleTimeoutMinutes() =>
        int.TryParse(configCache.GetValue("sandbox", "IdleTimeoutMinutes"), out var minutes) && minutes > 0
            ? minutes
            : settings.Value.IdleTimeoutMinutes;
}

/// <summary>Pure candidate-selection logic, split out from SandboxReaperHostedService so it's unit
/// testable without a database or a hosted service — the DeleteFiles decision is the part that's easy to
/// get backwards and would silently delete a user's session files, so it deserves tests that don't also
/// have to stand up EF Core and a fake ISandboxHostClient.</summary>
public static class SandboxReapPlanner
{
    public static IReadOnlyList<(SandboxInstance Instance, bool DeleteFiles)> Plan(
        IReadOnlyList<SandboxInstance> idleCandidates,
        IReadOnlyList<SandboxInstance> activeSessionSandboxes,
        IReadOnlySet<Guid> existingSessionOwnerIds)
    {
        var plan = new Dictionary<Guid, (SandboxInstance Instance, bool DeleteFiles)>();

        // Idle chat-session containers are retired but their files kept — returning to the session later
        // just spins a fresh container back onto the same host directory. Idle ephemeral sandboxes are a
        // hard-kill safety net (normal teardown is the explicit finally in Skill/AgentExecutionService),
        // so their scratch directory goes with them.
        foreach (var instance in idleCandidates)
            plan[instance.Id] = (instance, instance.Kind != SandboxKind.Session);

        // A session-kind sandbox whose owning ChatSession no longer exists always has its files deleted,
        // even if it isn't idle yet — this overrides any idle-sweep entry for the same instance above.
        foreach (var instance in activeSessionSandboxes)
        {
            if (!existingSessionOwnerIds.Contains(instance.OwnerId))
                plan[instance.Id] = (instance, true);
        }

        return [.. plan.Values];
    }
}
