using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Agents;

namespace PersonalAI.Api.BackgroundServices;

/// <summary>The deterministic half of Dashboard &amp; Alerts agents: every tick, runs each enabled agent's collection
/// jobs whose own interval has elapsed (the runner decides which are due from each job's last run), and prunes old
/// points and resolved alerts hourly. No model is involved. One agent failing does not stop the others, and a bad
/// tick does not end the loop.</summary>
public sealed class CollectionHostedService(
    IServiceScopeFactory scopeFactory,
    CollectionRunner runner,
    TimeProvider time,
    ILogger<CollectionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly Dictionary<Guid, DateTimeOffset> _lastPrune = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (true)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Collection tick failed.");
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

    private async Task TickAsync(CancellationToken ct)
    {
        List<(Guid Id, DashboardAlertsConfig Config)> agents;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var all = await scope.ServiceProvider.GetRequiredService<IAgentsRepository>().GetAllAsync(ct);
            agents = [.. all
                .Where(a => a.Type == AgentType.DashboardAlerts && a.Enabled)
                .Select(a => (a.Id, DashboardAlertsConfig.Parse(a.TypeConfigJson)))];
        }

        var now = time.GetUtcNow();
        foreach (var (id, config) in agents)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var result = await runner.RunAsync(id, JobSelection.Due, null, ct);
                if (result is { Skipped: false })
                    foreach (var failed in result.Results.Where(r => r.Error is not null))
                        logger.LogWarning("Job {Job} for agent {AgentId} failed on {Target}: {Error}", failed.Job, id, failed.Target, failed.Error);

                if (!_lastPrune.TryGetValue(id, out var pruned) || now - pruned >= PruneInterval)
                {
                    _lastPrune[id] = now;
                    var removed = await runner.PruneAsync(id, config, ct);
                    if (removed > 0) logger.LogInformation("Pruned {Count} old rows for agent {AgentId}.", removed, id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Collection for agent {AgentId} failed.", id);
            }
        }
    }
}
