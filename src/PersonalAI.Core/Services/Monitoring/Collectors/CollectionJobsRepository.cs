using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

public class CollectionJobsRepository(AppDbContext db) : ICollectionJobsRepository
{
    public async Task<IReadOnlyList<CollectionJob>> GetAllAsync(Guid agentId, CancellationToken ct) =>
        await db.CollectionJobs.Where(j => j.AgentId == agentId).OrderBy(j => j.Name).ToListAsync(ct);

    public async Task<CollectionJob?> GetAsync(Guid agentId, Guid jobId, CancellationToken ct) =>
        await db.CollectionJobs.FirstOrDefaultAsync(j => j.AgentId == agentId && j.Id == jobId, ct);

    public async Task<CollectionJob?> GetByNameAsync(Guid agentId, string name, CancellationToken ct)
    {
        var jobs = await db.CollectionJobs.Where(j => j.AgentId == agentId).ToListAsync(ct);
        return jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CollectionJob> AddAsync(CollectionJob job, CancellationToken ct)
    {
        db.CollectionJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task SaveAsync(CollectionJob job, CancellationToken ct)
    {
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        var job = await GetAsync(agentId, jobId, ct);
        if (job is null) return false;
        db.CollectionJobs.Remove(job);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task RecordRunAsync(Guid jobId, DateTimeOffset at, int rowCount, string? error, CancellationToken ct)
    {
        var job = await db.CollectionJobs.FindAsync([jobId], ct);
        if (job is null) return;

        job.LastRunAt = at;
        job.LastRowCount = rowCount;
        job.LastError = error is { Length: > 1000 } ? error[..997] + "..." : error;
        await db.SaveChangesAsync(ct);
    }
}
