using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Agents;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Monitoring;

/// <summary>An agent's collected data: the writes collection makes (points, datasets, pruning) and the reads behind
/// /api/agents/{id}/datasets. Datasets are created on first use, by agent and name.</summary>
public class DatasetService(AppDbContext db, IDatasetRepository datasets, IAgentsRepository agents)
{
    // Pruning removes in batches: after downtime, or when retention is shortened, the backlog can be millions of rows, and
    // loading them all into the change tracker (and one huge write transaction) would stall collection.
    private const int PruneBatchSize = 5000;

    public async Task<List<DatasetDto>> GetDatasets(Guid agentId, CancellationToken ct)
    {
        await EnsureAgent(agentId, ct);
        return (await datasets.ListAsync(agentId, ct))
            .Select(d => new DatasetDto(d.Name, d.Kind.ToString(), d.SchemaHint, d.PointCount, AsUtc(d.LastTs)))
            .ToList();
    }

    /// <summary>Points oldest-first. <paramref name="latest"/> returns each entity's newest point only.</summary>
    public async Task<List<DataPointDto>> GetPoints(
        Guid agentId, string name, string? entity, int? sinceMinutes, bool latest, int? limit, CancellationToken ct)
    {
        await EnsureAgent(agentId, ct);
        var from = sinceMinutes is > 0 ? DateTime.UtcNow.AddMinutes(-sinceMinutes.Value) : (DateTime?)null;
        var rows = await datasets.QueryAsync(agentId, name, entity, null, from, latest, Math.Clamp(limit ?? 500, 1, 5000), ct)
            ?? throw new NotFoundException($"No dataset named '{name}'.");
        return rows.Select(r => new DataPointDto(r.EntityKey, DateTime.SpecifyKind(r.Ts, DateTimeKind.Utc), r.ValuesJson)).ToList();
    }

    /// <summary>Appends points, creating any dataset that doesn't exist yet for the agent.</summary>
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

    /// <summary>Creates any missing dataset up front. Call before writing from parallel scopes, which would
    /// otherwise race to create the same (agent, name) row.</summary>
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

    /// <summary>Deletes points older than <paramref name="olderThan"/>. Returns the number of rows removed.</summary>
    public async Task<int> PruneAsync(Guid agentId, DateTime olderThan, CancellationToken ct)
    {
        var datasetIds = await db.Datasets.Where(d => d.AgentId == agentId).Select(d => d.Id).ToListAsync(ct);
        var removed = 0;
        // Snapshot datasets are pruned like any other. Keeping each entity's newest point forever made removed
        // containers and deleted servers look current for good; live widgets and alerts ignore stale rows anyway.
        while (true)
        {
            var batch = await db.DataPoints.Where(p => datasetIds.Contains(p.DatasetId) && p.Ts < olderThan).Take(PruneBatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            db.DataPoints.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            removed += batch.Count;
        }
        return removed;
    }

    private async Task EnsureAgent(Guid agentId, CancellationToken ct)
    {
        if (await agents.GetAsync(agentId, ct) is null) throw new NotFoundException($"Agent {agentId} not found.");
    }

    private static DateTime? AsUtc(DateTime? value)
    {
        return value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
    }
}
