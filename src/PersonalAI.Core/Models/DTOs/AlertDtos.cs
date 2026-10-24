namespace PersonalAI.Core.Models.DTOs;

public record AlertRuleDto(
    Guid Id, Guid AgentId, string Name, string? Description, string Dataset, string Field, string Operator,
    double Threshold, int ForMinutes, string Severity, string? Target, Dictionary<string, string>? Filter,
    bool Enabled, string Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record AlertDto(
    Guid Id, Guid AgentId, string AgentName, Guid RuleId, string RuleName, string Severity, string State,
    string EntityLabel, string Message, double Value, DateTime? FiredAt, DateTime LastSeen, DateTime? ResolvedAt,
    bool Acknowledged, DateTime? AcknowledgedAt);

/// <param name="Unacknowledged">Firing alerts that still need attention (what the nav badge counts).</param>
/// <param name="Acknowledged">Firing alerts someone has acknowledged; they stay listed until the condition clears.</param>
public record AlertSummaryDto(int Firing, int Critical, int Warning, int Unacknowledged, int UnacknowledgedCritical, int Acknowledged);

public record AcknowledgeAllResultDto(int Acknowledged);
