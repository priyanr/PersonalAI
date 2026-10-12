using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Api.BackgroundServices;

/// <summary>Polls for Agents whose NextRunAt has arrived and fires them unattended (AgentRunTrigger.Scheduled).
/// Each due agent runs in its own DI scope (repositories/DbContext are scoped, unlike this singleton hosted
/// service); one agent failing doesn't stop the others from firing in the same tick, and a bad tick doesn't
/// kill the scheduler loop.</summary>
public sealed class AgentSchedulerHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SchedulerSettings> settings,
    ILogger<AgentSchedulerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, settings.Value.PollIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        while (true)
        {
            try
            {
                await RunDueAgentsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Agent scheduler tick failed.");
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

    private async Task RunDueAgentsAsync(CancellationToken ct)
    {
        List<Guid> dueAgentIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var agentsRepo = scope.ServiceProvider.GetRequiredService<IAgentsRepository>();
            var due = await agentsRepo.GetDueAsync(DateTimeOffset.UtcNow, ct);
            dueAgentIds = due.Select(a => a.Id).ToList();
        }

        foreach (var agentId in dueAgentIds)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var execution = scope.ServiceProvider.GetRequiredService<IAgentExecutionService>();
                await foreach (var _ in execution.RunAsync(agentId, AgentRunTrigger.Scheduled, ct)) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled run of agent {AgentId} failed.", agentId);
            }
        }
    }
}
