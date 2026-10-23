using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring.Dashboards;

public class DashboardRepository(AppDbContext db) : IDashboardRepository
{
    public async Task<StoredDashboard?> GetCurrentAsync(Guid agentId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
        return dashboard is null ? null : await LoadAsync(dashboard, dashboard.CurrentVersion, ct);
    }

    public async Task<StoredDashboard?> GetVersionAsync(Guid agentId, int version, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
        return dashboard is null ? null : await LoadAsync(dashboard, version, ct);
    }

    public async Task<int> SaveAsync(Guid agentId, string specJson, string? reason, Guid? agentRunId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
        if (dashboard is null)
        {
            dashboard = new Dashboard { AgentId = agentId };
            db.Dashboards.Add(dashboard);
        }

        dashboard.CurrentVersion++;
        dashboard.UpdatedAt = DateTimeOffset.UtcNow;
        db.DashboardVersions.Add(new DashboardVersion
        {
            DashboardId = dashboard.Id,
            Version = dashboard.CurrentVersion,
            SpecJson = specJson,
            Reason = reason is { Length: > 500 } ? reason[..500] : reason,
            AgentRunId = agentRunId
        });

        var oldest = dashboard.CurrentVersion - IDashboardRepository.KeepVersions;
        if (oldest > 0)
            db.DashboardVersions.RemoveRange(await db.DashboardVersions
                .Where(v => v.DashboardId == dashboard.Id && v.Version <= oldest).ToListAsync(ct));

        await db.SaveChangesAsync(ct);
        return dashboard.CurrentVersion;
    }

    public async Task<IReadOnlyList<DashboardVersionInfo>> ListVersionsAsync(Guid agentId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.FirstOrDefaultAsync(d => d.AgentId == agentId, ct);
        if (dashboard is null) return [];

        var versions = await db.DashboardVersions.Where(v => v.DashboardId == dashboard.Id).ToListAsync(ct);
        return [.. versions.OrderByDescending(v => v.Version).Select(v => new DashboardVersionInfo(v.Version, v.Reason, v.AgentRunId, v.CreatedAt))];
    }

    private async Task<StoredDashboard?> LoadAsync(Dashboard dashboard, int version, CancellationToken ct)
    {
        var v = await db.DashboardVersions.FirstOrDefaultAsync(x => x.DashboardId == dashboard.Id && x.Version == version, ct);
        return v is null ? null : new StoredDashboard(v.Version, v.SpecJson, v.Reason, v.CreatedAt);
    }
}
