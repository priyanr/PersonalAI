using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring.Dashboards;

public record StoredDashboard(int Version, string SpecJson, string? Reason, DateTimeOffset UpdatedAt);

public interface IDashboardRepository
{
    /// <summary>The tracked dashboard row, or null if the agent has none yet.</summary>
    Task<Dashboard?> GetAsync(Guid agentId, CancellationToken ct);

    /// <summary>The current version, or null if the agent has no dashboard yet.</summary>
    Task<StoredDashboard?> GetCurrentAsync(Guid agentId, CancellationToken ct);

    /// <summary>Null if the dashboard or that version doesn't exist.</summary>
    Task<StoredDashboard?> GetVersionAsync(Guid agentId, int version, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<List<DashboardVersionDto>> ListVersionsAsync(Guid agentId, CancellationToken ct);
}

public class DashboardRepository(AppDbContext db) : IDashboardRepository
{
    public async Task<Dashboard?> GetAsync(Guid agentId, CancellationToken ct)
    {
        return await db.Dashboards.FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
    }

    public async Task<StoredDashboard?> GetCurrentAsync(Guid agentId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.AsNoTracking().FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
        return dashboard is null ? null : await LoadAsync(dashboard, dashboard.CurrentVersion, ct);
    }

    public async Task<StoredDashboard?> GetVersionAsync(Guid agentId, int version, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.AsNoTracking().FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
        return dashboard is null ? null : await LoadAsync(dashboard, version, ct);
    }

    public async Task<List<DashboardVersionDto>> ListVersionsAsync(Guid agentId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.AsNoTracking().FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
        if (dashboard is null) return [];

        var versions = await db.DashboardVersions.AsNoTracking().Where(v => v.DashboardId == dashboard.Id).ToListAsync(ct);
        return [.. versions.OrderByDescending(v => v.Version).Select(v => new DashboardVersionDto(v.Version, v.Reason, v.AgentRunId, v.CreatedAt))];
    }

    private async Task<StoredDashboard?> LoadAsync(Dashboard dashboard, int version, CancellationToken ct)
    {
        var v = await db.DashboardVersions.AsNoTracking().FirstOrDefaultAsync(x => x.DashboardId == dashboard.Id && x.Version == version, ct);
        return v is null ? null : new StoredDashboard(v.Version, v.SpecJson, v.Reason, v.CreatedAt);
    }
}
