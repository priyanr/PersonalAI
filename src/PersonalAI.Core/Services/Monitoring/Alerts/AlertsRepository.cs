using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring.Alerts;

public class AlertsRepository(AppDbContext db) : IAlertsRepository
{
    public async Task<IReadOnlyList<AlertRule>> GetRulesAsync(Guid agentId, CancellationToken ct) =>
        await db.AlertRules.Where(r => r.AgentId == agentId).OrderBy(r => r.Name).ToListAsync(ct);

    public async Task<AlertRule?> GetRuleAsync(Guid agentId, Guid ruleId, CancellationToken ct) =>
        await db.AlertRules.FirstOrDefaultAsync(r => r.AgentId == agentId && r.Id == ruleId, ct);

    public async Task<AlertRule?> GetRuleByNameAsync(Guid agentId, string name, CancellationToken ct)
    {
        // Compared client-side so the match is case-insensitive on every provider.
        var rules = await db.AlertRules.Where(r => r.AgentId == agentId).ToListAsync(ct);
        return rules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<AlertRule> AddRuleAsync(AlertRule rule, CancellationToken ct)
    {
        db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task SaveRuleAsync(AlertRule rule, CancellationToken ct)
    {
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteRuleAsync(Guid agentId, Guid ruleId, CancellationToken ct)
    {
        var rule = await GetRuleAsync(agentId, ruleId, ct);
        if (rule is null) return false;

        db.AlertEvents.RemoveRange(await db.AlertEvents.Where(e => e.RuleId == ruleId).ToListAsync(ct));
        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AlertEvent>> GetOpenEventsAsync(Guid ruleId, CancellationToken ct) =>
        await db.AlertEvents.Where(e => e.RuleId == ruleId && e.State != AlertState.Resolved).ToListAsync(ct);

    public async Task AddEventAsync(AlertEvent alert, CancellationToken ct)
    {
        db.AlertEvents.Add(alert);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveEventsAsync(IEnumerable<AlertEvent> remove, CancellationToken ct)
    {
        db.AlertEvents.RemoveRange(remove);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AlertEvent>> GetEventsAsync(Guid? agentId, bool firing, DateTime? resolvedSince, CancellationToken ct)
    {
        // The state and date filters run in the database: this is polled by the UI, and resolved history can be large.
        var query = db.AlertEvents.AsQueryable();
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

    public async Task<bool> AcknowledgeAsync(Guid eventId, CancellationToken ct)
    {
        var alert = await db.AlertEvents.FindAsync([eventId], ct);
        if (alert is null || alert.State == AlertState.Pending) return false;

        if (!alert.Acknowledged)
        {
            alert.Acknowledged = true;
            alert.AcknowledgedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public async Task<bool> UnacknowledgeAsync(Guid eventId, CancellationToken ct)
    {
        var alert = await db.AlertEvents.FindAsync([eventId], ct);
        if (alert is null || alert.State == AlertState.Pending) return false;

        if (alert.Acknowledged)
        {
            alert.Acknowledged = false;
            alert.AcknowledgedAt = null;
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public async Task<int> AcknowledgeAllAsync(Guid? agentId, CancellationToken ct)
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
        return open.Count;
    }

    public async Task ResolveOpenEventsAsync(Guid ruleId, DateTime now, string reason, CancellationToken ct)
    {
        var open = await GetOpenEventsAsync(ruleId, ct);
        foreach (var e in open)
        {
            if (e.State == AlertState.Pending)
            {
                db.AlertEvents.Remove(e);
                continue;
            }
            e.State = AlertState.Resolved;
            e.ResolvedAt = now;
            e.Message = Truncate($"{e.Message} — resolved: {reason}");
        }
        await db.SaveChangesAsync(ct);
    }

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

    internal static string Truncate(string text) => text.Length <= 500 ? text : text[..497] + "...";
}
