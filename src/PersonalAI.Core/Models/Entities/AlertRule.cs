namespace PersonalAI.Core.Models.Entities;

public static class AlertSeverity
{
    public const string Warning = "warning", Critical = "critical";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Warning, Critical };
}

public static class AlertOperators
{
    public const string Gt = "gt", Gte = "gte", Lt = "lt", Lte = "lte", Eq = "eq", Ne = "ne";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Gt, Gte, Lt, Lte, Eq, Ne };

    public static bool Compare(double value, string op, double threshold) => op switch
    {
        Gt => value > threshold,
        Gte => value >= threshold,
        Lt => value < threshold,
        Lte => value <= threshold,
        Eq => Math.Abs(value - threshold) < 1e-9,
        Ne => Math.Abs(value - threshold) >= 1e-9,
        _ => false
    };

    public static string Symbol(string op) => op switch
    {
        Gt => ">", Gte => "≥", Lt => "<", Lte => "≤", Eq => "=", Ne => "≠", _ => op
    };
}

public static class AlertSources
{
    public const string User = "user", Llm = "llm";
}

/// <summary>A condition on one numeric (or boolean, read as 1/0) field of an agent's dataset, evaluated for
/// every matching entity after each collection pass. "available eq 0" on data_source_status alerts on a connection that is down; "cpuPercent gte 90
/// for 10 minutes" on sustained load.</summary>
public class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    public required string Dataset { get; set; }
    public required string Field { get; set; }
    public required string Operator { get; set; }
    public double Threshold { get; set; }

    /// <summary>The condition must hold continuously this long (measured in data-point time) before the
    /// alert fires; 0 fires on the first violating sample.</summary>
    public int ForMinutes { get; set; }

    public string Severity { get; set; } = AlertSeverity.Warning;

    /// <summary>Limit to one connection (server, database...) by name.</summary>
    public string? Target { get; set; }

    /// <summary>JSON object of field equality filters, e.g. {"mount":"/"}.</summary>
    public string? FilterJson { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>"user" or "llm": who authored it. A model may edit or delete only its own rules.</summary>
    public string Source { get; set; } = AlertSources.User;

    /// <summary>Reserved for notifications: a skill or tool to run when an alert fires or resolves (a later
    /// step). Stored now so adding it needs no schema change.</summary>
    public string? ActionJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AlertState
{
    /// <summary>The condition holds but not yet for the rule's ForMinutes. Not shown to users.</summary>
    Pending = 0,
    Firing = 1,
    Resolved = 2
}

/// <summary>One occurrence of a rule's condition for one entity. At most one non-resolved event exists per
/// (rule, entity); resolved events are history. Times are UTC DateTime so SQLite can compare them.</summary>
public class AlertEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public Guid AgentId { get; set; }

    public required string EntityKey { get; set; }
    public required string EntityLabel { get; set; }

    public AlertState State { get; set; }
    public double Value { get; set; }
    public required string Message { get; set; }

    /// <summary>When the condition was first seen violated (the start of a Pending period).</summary>
    public DateTime FirstSeen { get; set; }
    public DateTime? FiredAt { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public bool Acknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Reserved for notifications (a later step): the outcome of running the rule's action.</summary>
    public string? ActionStatus { get; set; }
}
