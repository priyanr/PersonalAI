namespace PersonalAI.Core.Models.Entities;

public static class JobStatus
{
    /// <summary>Created by the model and waiting for the user to approve the exact request. Never runs unattended.</summary>
    public const string PendingApproval = "pending_approval";
    public const string Active = "active";
    public const string Disabled = "disabled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { PendingApproval, Active, Disabled };
}

public static class JobSources
{
    public const string User = "user", Llm = "llm";
}

/// <summary>A saved, repeatable data collection for one agent: send a request to the agent's connections on an interval,
/// parse the output into rows, and append them to a dataset. Designed by the model and run by the platform with no model involved. Only Active jobs run.</summary>
public class CollectionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Which data tool runs the request (its tool name, e.g. "ssh").</summary>
    public required string Tool { get; set; }

    /// <summary>The request to send (a command for ssh, a query for a database tool).</summary>
    public required string Request { get; set; }

    public int IntervalMinutes { get; set; } = 5;

    /// <summary>ParserSpec as JSON.</summary>
    public required string ParserJson { get; set; }

    /// <summary>DatasetSpec as JSON.</summary>
    public string DatasetJson { get; set; } = "{}";

    /// <summary>JSON array of connection names to run on; null means every enabled connection in the agent's scope for the tool.</summary>
    public string? TargetsJson { get; set; }

    /// <summary>The request is not on the tool's allowed list. It exists only because the person approved this exact text,
    /// and is reset whenever the request changes. Never set for a request the tool allows.</summary>
    public bool OutsideAllowlist { get; set; }

    public string Status { get; set; } = JobStatus.PendingApproval;
    public string Source { get; set; } = JobSources.Llm;

    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastError { get; set; }
    public int? LastRowCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Records one run. The error is capped at 1000 characters: it comes from a remote command and is shown in the UI.</summary>
    public void RecordRun(DateTimeOffset at, int rowCount, string? error)
    {
        LastRunAt = at;
        LastRowCount = rowCount;
        LastError = error is { Length: > 1000 } ? error[..997] + "..." : error;
    }
}
