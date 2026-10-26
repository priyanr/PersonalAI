namespace PersonalAI.Core.Models.Entities;

/// <summary>The single dashboard of a Dashboard &amp; Alerts agent. The content lives in versions; this row
/// only names the current one.</summary>
public class Dashboard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public int CurrentVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One immutable saved spec. Restoring an old version saves a new version with the old content,
/// so history is never rewritten.</summary>
public class DashboardVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DashboardId { get; set; }
    public int Version { get; set; }
    public required string SpecJson { get; set; }

    /// <summary>Why this version was saved, as written by the model (or "Restored vN").</summary>
    public string? Reason { get; set; }

    /// <summary>The agent run that saved it; null for restores.</summary>
    public Guid? AgentRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
