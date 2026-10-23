using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

public interface ICollectionJobsRepository
{
    Task<IReadOnlyList<CollectionJob>> GetAllAsync(Guid agentId, CancellationToken ct);
    Task<CollectionJob?> GetAsync(Guid agentId, Guid jobId, CancellationToken ct);
    /// <summary>Case-insensitive.</summary>
    Task<CollectionJob?> GetByNameAsync(Guid agentId, string name, CancellationToken ct);
    Task<CollectionJob> AddAsync(CollectionJob job, CancellationToken ct);
    /// <summary>Persists changes made to a job returned by this repository.</summary>
    Task SaveAsync(CollectionJob job, CancellationToken ct);
    Task<bool> DeleteAsync(Guid agentId, Guid jobId, CancellationToken ct);

    /// <summary>Records the outcome of a run on the job.</summary>
    Task RecordRunAsync(Guid jobId, DateTimeOffset at, int rowCount, string? error, CancellationToken ct);
}
