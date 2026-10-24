using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring;

/// <summary>A point to append. The dataset is created on first use (by agent + name).</summary>
public record NewDataPoint(string Dataset, DatasetKind Kind, string EntityKey, DateTime Ts, string ValuesJson);

public record DatasetDefinition(string Name, DatasetKind Kind, string? SchemaHint);

public record DatasetSummary(string Name, DatasetKind Kind, string? SchemaHint, long PointCount, DateTime? LastTs);

public record DataPointRow(string EntityKey, DateTime Ts, string ValuesJson);

public interface IDatasetRepository
{
    Task<IReadOnlyList<DatasetSummary>> ListAsync(Guid agentId, CancellationToken ct);

    /// <summary>Points for one dataset, oldest first. <paramref name="latestPerEntity"/> returns only each
    /// entity's newest point (ignores <paramref name="from"/>). Returns null if the dataset doesn't exist.</summary>
    Task<IReadOnlyList<DataPointRow>?> QueryAsync(
        Guid agentId, string dataset, string? entityKey, string? entityPrefix, DateTime? from, bool latestPerEntity, int limit, CancellationToken ct);
}

public class DatasetRepository(AppDbContext db) : IDatasetRepository
{
    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(Guid agentId, CancellationToken ct)
    {
        var datasets = await db.Datasets.AsNoTracking().Where(d => d.AgentId == agentId).OrderBy(d => d.Name).ToListAsync(ct);
        var ids = datasets.Select(d => d.Id).ToList();
        var stats = await db.DataPoints
            .Where(p => ids.Contains(p.DatasetId))
            .GroupBy(p => p.DatasetId)
            .Select(g => new { DatasetId = g.Key, Count = g.LongCount(), Last = g.Max(p => p.Ts) })
            .ToDictionaryAsync(s => s.DatasetId, ct);
        return datasets
            .Select(d => stats.TryGetValue(d.Id, out var s)
                ? new DatasetSummary(d.Name, d.Kind, d.SchemaHint, s.Count, s.Last)
                : new DatasetSummary(d.Name, d.Kind, d.SchemaHint, 0, null))
            .ToList();
    }

    public async Task<IReadOnlyList<DataPointRow>?> QueryAsync(
        Guid agentId, string dataset, string? entityKey, string? entityPrefix, DateTime? from, bool latestPerEntity, int limit, CancellationToken ct)
    {
        var ds = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.AgentId == agentId && d.Name == dataset, ct);
        if (ds is null) return null;

        var query = db.DataPoints.AsNoTracking().Where(p => p.DatasetId == ds.Id);
        if (entityKey is not null) query = query.Where(p => p.EntityKey == entityKey);
        if (entityPrefix is not null) query = query.Where(p => p.EntityKey.StartsWith(entityPrefix));

        if (latestPerEntity)
        {
            query = query.Where(p => p.Ts == db.DataPoints
                .Where(q => q.DatasetId == p.DatasetId && q.EntityKey == p.EntityKey)
                .Max(q => q.Ts));
            var latest = await query.OrderBy(p => p.EntityKey).Take(limit).ToListAsync(ct);
            return [.. latest.Select(ToRow)];
        }

        if (from is not null) query = query.Where(p => p.Ts >= from);
        // Newest-first so Take keeps the most recent points when a window exceeds the limit.
        var rows = await query.OrderByDescending(p => p.Ts).Take(limit).ToListAsync(ct);
        rows.Reverse();
        return [.. rows.Select(ToRow)];
    }

    private static DataPointRow ToRow(DataPoint p)
    {
        return new(p.EntityKey, p.Ts, p.ValuesJson);
    }
}
