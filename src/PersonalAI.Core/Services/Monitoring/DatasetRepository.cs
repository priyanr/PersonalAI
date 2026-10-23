using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring;

public class DatasetRepository(AppDbContext db) : IDatasetRepository
{
    public async Task AddPointsAsync(Guid agentId, IReadOnlyCollection<NewDataPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return;

        var existing = await db.Datasets.Where(d => d.AgentId == agentId).ToDictionaryAsync(d => d.Name, ct);
        foreach (var group in points.GroupBy(p => p.Dataset))
        {
            if (!existing.TryGetValue(group.Key, out var dataset))
            {
                dataset = new Dataset { AgentId = agentId, Name = group.Key, Kind = group.First().Kind };
                db.Datasets.Add(dataset);
                existing[group.Key] = dataset;
            }
            db.DataPoints.AddRange(group.Select(p => new DataPoint
            {
                DatasetId = dataset.Id, EntityKey = p.EntityKey, Ts = p.Ts, ValuesJson = p.ValuesJson
            }));
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task EnsureDatasetsAsync(Guid agentId, IReadOnlyCollection<DatasetDefinition> definitions, CancellationToken ct)
    {
        var existing = await db.Datasets.Where(d => d.AgentId == agentId).ToDictionaryAsync(d => d.Name, ct);
        var changed = false;
        foreach (var def in definitions)
        {
            if (existing.TryGetValue(def.Name, out var dataset))
            {
                if (dataset.SchemaHint != def.SchemaHint) { dataset.SchemaHint = def.SchemaHint; changed = true; }
                continue;
            }
            db.Datasets.Add(new Dataset { AgentId = agentId, Name = def.Name, Kind = def.Kind, SchemaHint = def.SchemaHint });
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(Guid agentId, CancellationToken ct)
    {
        var datasets = await db.Datasets.Where(d => d.AgentId == agentId).OrderBy(d => d.Name).ToListAsync(ct);
        var summaries = new List<DatasetSummary>();
        foreach (var d in datasets)
        {
            var count = await db.DataPoints.LongCountAsync(p => p.DatasetId == d.Id, ct);
            DateTime? last = count == 0 ? null : await db.DataPoints.Where(p => p.DatasetId == d.Id).MaxAsync(p => p.Ts, ct);
            summaries.Add(new DatasetSummary(d.Name, d.Kind, d.SchemaHint, count, last));
        }
        return summaries;
    }

    public async Task<IReadOnlyList<DataPointRow>?> QueryAsync(
        Guid agentId, string dataset, string? entityKey, string? entityPrefix, DateTime? from, bool latestPerEntity, int limit, CancellationToken ct)
    {
        var ds = await db.Datasets.FirstOrDefaultAsync(d => d.AgentId == agentId && d.Name == dataset, ct);
        if (ds is null) return null;

        var query = db.DataPoints.Where(p => p.DatasetId == ds.Id);
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

    // Pruning removes in batches: after downtime, or when retention is shortened, the backlog can be millions of rows, and
    // loading them all into the change tracker (and one huge write transaction) would stall collection.
    private const int PruneBatchSize = 5000;

    public async Task<int> PruneAsync(Guid agentId, DateTime olderThan, CancellationToken ct)
    {
        var datasets = await db.Datasets.Where(d => d.AgentId == agentId).ToListAsync(ct);
        var removed = 0;
        foreach (var d in datasets)
        {
            // Snapshot datasets are pruned like any other. Keeping each entity's newest point forever made removed
            // containers and deleted servers look current for good; live widgets and alerts ignore stale rows anyway.
            while (true)
            {
                var batch = await db.DataPoints.Where(p => p.DatasetId == d.Id && p.Ts < olderThan).Take(PruneBatchSize).ToListAsync(ct);
                if (batch.Count == 0) break;
                db.DataPoints.RemoveRange(batch);
                await db.SaveChangesAsync(ct);
                removed += batch.Count;
            }
        }
        return removed;
    }

    private static DataPointRow ToRow(DataPoint p) => new(p.EntityKey, p.Ts, p.ValuesJson);
}
