namespace PersonalAI.Core.Interfaces;

public record DashboardVersionInfo(int Version, string? Reason, Guid? AgentRunId, DateTimeOffset CreatedAt);

public record StoredDashboard(int Version, string SpecJson, string? Reason, DateTimeOffset UpdatedAt);

public interface IDashboardRepository
{
    /// <summary>The current version, or null if the agent has no dashboard yet.</summary>
    Task<StoredDashboard?> GetCurrentAsync(Guid agentId, CancellationToken ct);

    /// <summary>Null if the dashboard or that version doesn't exist.</summary>
    Task<StoredDashboard?> GetVersionAsync(Guid agentId, int version, CancellationToken ct);

    /// <summary>Saves a new version (creating the dashboard on first save) and makes it current.
    /// Only the newest <see cref="KeepVersions"/> versions are retained. Returns the new version number.</summary>
    Task<int> SaveAsync(Guid agentId, string specJson, string? reason, Guid? agentRunId, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<DashboardVersionInfo>> ListVersionsAsync(Guid agentId, CancellationToken ct);

    public const int KeepVersions = 50;
}
