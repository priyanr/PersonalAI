using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

/// <param name="Unacknowledged">Firing alerts nobody has acknowledged yet: what needs attention.</param>
/// <param name="UnacknowledgedCritical">How many of those are critical.</param>
public record AlertSummary(int Firing, int Critical, int Warning, int Unacknowledged, int UnacknowledgedCritical);

public interface IAlertsRepository
{
    // ---- rules ----
    Task<IReadOnlyList<AlertRule>> GetRulesAsync(Guid agentId, CancellationToken ct);
    Task<AlertRule?> GetRuleAsync(Guid agentId, Guid ruleId, CancellationToken ct);
    Task<AlertRule?> GetRuleByNameAsync(Guid agentId, string name, CancellationToken ct);
    Task<AlertRule> AddRuleAsync(AlertRule rule, CancellationToken ct);
    /// <summary>Persists changes made to a tracked rule returned by this repository.</summary>
    Task SaveRuleAsync(AlertRule rule, CancellationToken ct);
    /// <summary>Also deletes the rule's events. False if no such rule.</summary>
    Task<bool> DeleteRuleAsync(Guid agentId, Guid ruleId, CancellationToken ct);

    // ---- events ----
    /// <summary>Pending and Firing events for the rule, for the evaluator.</summary>
    Task<IReadOnlyList<AlertEvent>> GetOpenEventsAsync(Guid ruleId, CancellationToken ct);
    Task AddEventAsync(AlertEvent alert, CancellationToken ct);
    /// <summary>Persists changes to tracked events; <paramref name="remove"/> deletes the given events.</summary>
    Task SaveEventsAsync(IEnumerable<AlertEvent> remove, CancellationToken ct);

    /// <summary>Firing events (and Resolved ones since <paramref name="resolvedSince"/> when set), newest first.
    /// Pending events are never returned. Optionally limited to one agent.</summary>
    Task<IReadOnlyList<AlertEvent>> GetEventsAsync(Guid? agentId, bool firing, DateTime? resolvedSince, CancellationToken ct);

    /// <summary>Counts of firing alerts across all agents, for the nav badge.</summary>
    Task<AlertSummary> GetSummaryAsync(CancellationToken ct);

    /// <summary>Marks an alert as seen. It stays acknowledged until it resolves; if the condition returns later that is a new,
    /// unacknowledged alert. False if there is no such (non-pending) alert.</summary>
    Task<bool> AcknowledgeAsync(Guid eventId, CancellationToken ct);

    /// <summary>Takes an acknowledgement back (the alert needs attention again). False if there is no such alert.</summary>
    Task<bool> UnacknowledgeAsync(Guid eventId, CancellationToken ct);

    /// <summary>Acknowledges every unacknowledged firing alert, optionally of one agent. Returns how many changed.</summary>
    Task<int> AcknowledgeAllAsync(Guid? agentId, CancellationToken ct);

    /// <summary>Resolves the rule's open events (used when a rule is disabled). Pending ones are dropped.</summary>
    Task ResolveOpenEventsAsync(Guid ruleId, DateTime now, string reason, CancellationToken ct);

    /// <summary>Deletes resolved events older than <paramref name="olderThan"/>. Returns the number removed.</summary>
    Task<int> PruneResolvedAsync(Guid agentId, DateTime olderThan, CancellationToken ct);
}
