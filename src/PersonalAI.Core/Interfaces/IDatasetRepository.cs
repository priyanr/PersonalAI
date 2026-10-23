using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

/// <summary>A point to append. The dataset is created on first use (by agent + name).</summary>
public record NewDataPoint(string Dataset, DatasetKind Kind, string EntityKey, DateTime Ts, string ValuesJson);

public record DatasetDefinition(string Name, DatasetKind Kind, string? SchemaHint);

public record DatasetSummary(string Name, DatasetKind Kind, string? SchemaHint, long PointCount, DateTime? LastTs);

public record DataPointRow(string EntityKey, DateTime Ts, string ValuesJson);

public interface IDatasetRepository
{
    /// <summary>Appends points, creating any dataset that doesn't exist yet for the agent.</summary>
    Task AddPointsAsync(Guid agentId, IReadOnlyCollection<NewDataPoint> points, CancellationToken ct);

    /// <summary>Creates any missing dataset up front. Call before writing from parallel scopes, which would
    /// otherwise race to create the same (agent, name) row.</summary>
    Task EnsureDatasetsAsync(Guid agentId, IReadOnlyCollection<DatasetDefinition> definitions, CancellationToken ct);

    Task<IReadOnlyList<DatasetSummary>> ListAsync(Guid agentId, CancellationToken ct);

    /// <summary>Points for one dataset, oldest first. <paramref name="latestPerEntity"/> returns only each
    /// entity's newest point (ignores <paramref name="from"/>). Returns null if the dataset doesn't exist.</summary>
    Task<IReadOnlyList<DataPointRow>?> QueryAsync(
        Guid agentId, string dataset, string? entityKey, string? entityPrefix, DateTime? from, bool latestPerEntity, int limit, CancellationToken ct);

    /// <summary>Deletes points older than <paramref name="olderThan"/>. Returns the number of rows removed.</summary>
    Task<int> PruneAsync(Guid agentId, DateTime olderThan, CancellationToken ct);
}
