using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring.Alerts;

/// <param name="Unacknowledged">Firing alerts nobody has acknowledged yet: what needs attention.</param>
/// <param name="UnacknowledgedCritical">How many of those are critical.</param>
public record AlertSummary(int Firing, int Critical, int Warning, int Unacknowledged, int UnacknowledgedCritical);

public interface IAlertsRepository
{
    // ---- rules ----
    Task<List<AlertRule>> GetRulesAsync(Guid agentId, CancellationToken ct);
    /// <summary>Every rule of the given agents, for naming their alerts.</summary>
    Task<List<AlertRule>> GetRulesOfAgentsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct);
    Task<AlertRule?> GetRuleAsync(Guid agentId, Guid ruleId, CancellationToken ct);
    Task<AlertRule?> GetRuleByNameAsync(Guid agentId, string name, CancellationToken ct);

    // ---- events ----
    /// <summary>Pending and Firing events for the rule, tracked, for the evaluator and for closing them.</summary>
    Task<List<AlertEvent>> GetOpenEventsAsync(Guid ruleId, CancellationToken ct);

    /// <summary>The tracked event, or null.</summary>
    Task<AlertEvent?> GetEventAsync(Guid eventId, CancellationToken ct);

    /// <summary>Firing events (and Resolved ones since <paramref name="resolvedSince"/> when set), newest first.
    /// Pending events are never returned. Optionally limited to one agent.</summary>
    Task<List<AlertEvent>> GetEventsAsync(Guid? agentId, bool firing, DateTime? resolvedSince, CancellationToken ct);

    /// <summary>Counts of firing alerts across all agents, for the nav badge.</summary>
    Task<AlertSummary> GetSummaryAsync(CancellationToken ct);
}

public class AlertsRepository(AppDbContext db) : IAlertsRepository
{
    public async Task<List<AlertRule>> GetRulesAsync(Guid agentId, CancellationToken ct)
    {
        return await db.AlertRules.Where(r => r.AgentId == agentId).OrderBy(r => r.Name).ToListAsync(ct);
    }

    public async Task<List<AlertRule>> GetRulesOfAgentsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct)
    {
        return await db.AlertRules.AsNoTracking().Where(r => agentIds.Contains(r.AgentId)).ToListAsync(ct);
    }

    public async Task<AlertRule?> GetRuleAsync(Guid agentId, Guid ruleId, CancellationToken ct)
    {
        return await db.AlertRules.FirstOrDefaultAsync(r => r.AgentId == agentId && r.Id == ruleId, ct);
    }

    public async Task<AlertRule?> GetRuleByNameAsync(Guid agentId, string name, CancellationToken ct)
    {
        // Compared client-side so the match is case-insensitive on every provider.
        var rules = await db.AlertRules.Where(r => r.AgentId == agentId).ToListAsync(ct);
        return rules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<AlertEvent>> GetOpenEventsAsync(Guid ruleId, CancellationToken ct)
    {
        return await db.AlertEvents.Where(e => e.RuleId == ruleId && e.State != AlertState.Resolved).ToListAsync(ct);
    }

    public async Task<AlertEvent?> GetEventAsync(Guid eventId, CancellationToken ct)
    {
        return await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
    }

    public async Task<List<AlertEvent>> GetEventsAsync(Guid? agentId, bool firing, DateTime? resolvedSince, CancellationToken ct)
    {
        // The state and date filters run in the database: this is polled by the UI, and resolved history can be large.
        var query = db.AlertEvents.AsNoTracking();
        if (agentId is not null) query = query.Where(e => e.AgentId == agentId);

        if (firing && resolvedSince is { } since)
            query = query.Where(e => e.State == AlertState.Firing || (e.State == AlertState.Resolved && e.ResolvedAt >= since));
        else if (firing)
            query = query.Where(e => e.State == AlertState.Firing);
        else if (resolvedSince is { } onlySince)
            query = query.Where(e => e.State == AlertState.Resolved && e.ResolvedAt >= onlySince);
        else
            return [];

        var events = await query.ToListAsync(ct);
        return [.. events
            .OrderByDescending(e => e.State == AlertState.Firing)
            .ThenByDescending(e => e.FiredAt ?? e.FirstSeen)];
    }

    public async Task<AlertSummary> GetSummaryAsync(CancellationToken ct)
    {
        // One joined query over firing events only, instead of loading every event and each agent's rules.
        var firing = await (from e in db.AlertEvents
                            where e.State == AlertState.Firing
                            join r in db.AlertRules on e.RuleId equals r.Id
                            select new { r.Severity, e.Acknowledged }).ToListAsync(ct);
        var critical = firing.Count(f => f.Severity == AlertSeverity.Critical);
        var unacknowledged = firing.Where(f => !f.Acknowledged).ToList();
        return new AlertSummary(firing.Count, critical, firing.Count - critical, unacknowledged.Count,
            unacknowledged.Count(f => f.Severity == AlertSeverity.Critical));
    }
}
