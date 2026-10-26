using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

public interface ICollectionJobsRepository
{
    Task<List<CollectionJob>> GetAllAsync(Guid agentId, CancellationToken ct);
    Task<CollectionJob?> GetAsync(Guid agentId, Guid jobId, CancellationToken ct);
    /// <summary>Case-insensitive.</summary>
    Task<CollectionJob?> GetByNameAsync(Guid agentId, string name, CancellationToken ct);
}

public class CollectionJobsRepository(AppDbContext db) : ICollectionJobsRepository
{
    public async Task<List<CollectionJob>> GetAllAsync(Guid agentId, CancellationToken ct)
    {
        return await db.CollectionJobs.AsNoTracking().Where(j => j.AgentId == agentId).OrderBy(j => j.Name).ToListAsync(ct);
    }

    public async Task<CollectionJob?> GetAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        return await db.CollectionJobs.FirstOrDefaultAsync(j => j.AgentId == agentId && j.Id == jobId, ct);
    }

    public async Task<CollectionJob?> GetByNameAsync(Guid agentId, string name, CancellationToken ct)
    {
        var jobs = await db.CollectionJobs.Where(j => j.AgentId == agentId).ToListAsync(ct);
        return jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
