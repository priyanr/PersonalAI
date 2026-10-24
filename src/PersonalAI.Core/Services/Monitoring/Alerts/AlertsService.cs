using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Agents;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring.Alerts;

/// <summary>The alerts rules produce (/api/alerts): listing, the nav badge summary, acknowledging, and pruning old
/// resolved ones. Rules are AlertRuleService; deciding what fires is AlertEvaluator.</summary>
public class AlertsService(AppDbContext db, IAlertsRepository alerts, IAgentsRepository agents)
{
    /// <summary><paramref name="state"/>: <c>active</c> (default, firing), <c>resolved</c> (last 7 days) or <c>all</c>.
    /// Firing first, then newest.</summary>
    public async Task<List<AlertDto>> GetAlerts(string? state, Guid? agentId, CancellationToken ct)
    {
        var active = state is null or "active" or "all";
        var resolved = state is "resolved" or "all";
        if (!active && !resolved) throw new InvalidRequestException("state must be active, resolved or all.");

        var events = await alerts.GetEventsAsync(agentId, active, resolved ? DateTime.UtcNow.AddDays(-7) : null, ct);
        if (events.Count == 0) return [];

        var agentNames = (await agents.GetAllAsync(ct)).ToDictionary(a => a.Id, a => a.Name);
        var rules = (await alerts.GetRulesOfAgentsAsync(events.Select(e => e.AgentId).Distinct().ToList(), ct)).ToDictionary(r => r.Id);

        return events.Select(e =>
            {
                var rule = rules.GetValueOrDefault(e.RuleId);
                return new AlertDto(e.Id, e.AgentId, agentNames.GetValueOrDefault(e.AgentId, "(deleted agent)"), e.RuleId,
                    rule?.Name ?? "(deleted rule)", rule?.Severity ?? AlertSeverity.Warning, e.State.ToString().ToLowerInvariant(),
                    e.EntityLabel, e.Message, e.Value, e.FiredAt, e.LastSeen, e.ResolvedAt, e.Acknowledged, e.AcknowledgedAt);
            })
            // Critical before warning within the firing group; the repository already ordered by recency.
            .OrderByDescending(a => a.State == "firing").ThenByDescending(a => a.Severity == AlertSeverity.Critical)
            .ToList();
    }

    public async Task<AlertSummaryDto> GetSummary(CancellationToken ct)
    {
        var s = await alerts.GetSummaryAsync(ct);
        return new AlertSummaryDto(s.Firing, s.Critical, s.Warning, s.Unacknowledged, s.UnacknowledgedCritical, s.Firing - s.Unacknowledged);
    }

    /// <summary>Marks an alert as seen. It stays acknowledged until it resolves; if the condition returns later that is a new,
    /// unacknowledged alert.</summary>
    public async Task Acknowledge(Guid eventId, CancellationToken ct)
    {
        var alert = await GetVisibleEvent(eventId, ct);
        if (alert.Acknowledged) return;

        alert.Acknowledged = true;
        alert.AcknowledgedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes an acknowledgement back, so the alert needs attention again.</summary>
    public async Task Unacknowledge(Guid eventId, CancellationToken ct)
    {
        var alert = await GetVisibleEvent(eventId, ct);
        if (!alert.Acknowledged) return;

        alert.Acknowledged = false;
        alert.AcknowledgedAt = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Acknowledges every unacknowledged firing alert, optionally of one agent.</summary>
    public async Task<AcknowledgeAllResultDto> AcknowledgeAll(Guid? agentId, CancellationToken ct)
    {
        var query = db.AlertEvents.Where(e => e.State == AlertState.Firing && !e.Acknowledged);
        if (agentId is not null) query = query.Where(e => e.AgentId == agentId);

        var open = await query.ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var e in open)
        {
            e.Acknowledged = true;
            e.AcknowledgedAt = now;
        }
        if (open.Count > 0) await db.SaveChangesAsync(ct);
        return new AcknowledgeAllResultDto(open.Count);
    }

    /// <summary>Deletes resolved alerts older than <paramref name="olderThan"/>. Returns the number removed.</summary>
    public async Task<int> PruneResolvedAsync(Guid agentId, DateTime olderThan, CancellationToken ct)
    {
        // In batches, so a large backlog is never loaded into the change tracker or removed in one transaction.
        const int batchSize = 5000;
        var removed = 0;
        while (true)
        {
            var batch = await db.AlertEvents
                .Where(e => e.AgentId == agentId && e.State == AlertState.Resolved && e.ResolvedAt < olderThan)
                .Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            db.AlertEvents.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            removed += batch.Count;
        }
        return removed;
    }

    // A pending alert hasn't fired yet: the UI never lists it, so it can't be acknowledged either.
    private async Task<AlertEvent> GetVisibleEvent(Guid eventId, CancellationToken ct)
    {
        var alert = await alerts.GetEventAsync(eventId, ct);
        return alert is null || alert.State == AlertState.Pending ? throw new NotFoundException($"Alert {eventId} not found.") : alert;
    }
}
