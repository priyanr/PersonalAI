using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Agents;
using Microsoft.EntityFrameworkCore;
using PersonalAI.Core.Services.Monitoring.Collectors;

namespace PersonalAI.Core.Services.Monitoring.Dashboards;

public record DashboardValidation(DashboardSpec? Spec, IReadOnlyList<string> Errors)
{
    public bool IsValid => Spec is not null && Errors.Count == 0;
}

/// <summary>Validates, saves and renders an agent's dashboard. Validation is two-stage: the structural rules
/// in <see cref="DashboardSpecValidator"/>, then a check of every referenced dataset, field, column, filter
/// key and connection name against what has actually been collected, so a model gets precise feedback ("field
/// 'cpu' not found; available: …") instead of saving a dashboard that renders empty.</summary>
public sealed class DashboardService(
    AppDbContext db, IDashboardRepository repo, IDatasetRepository datasets, ICollectionJobsRepository jobs, IAgentsRepository agents, TimeProvider time)
{
    private const int SampleSize = 200;

    /// <summary>Only the newest this many versions are kept.</summary>
    public const int KeepVersions = 50;

    public async Task<DashboardValidation> ValidateAsync(Guid agentId, string specJson, CancellationToken ct)
    {
        var (spec, parseError) = DashboardSpecJson.Parse(specJson);
        if (spec is null) return new DashboardValidation(null, [parseError!]);

        var errors = DashboardSpecValidator.Validate(spec);
        if (errors.Count > 0) return new DashboardValidation(spec, errors);

        // Datasets that exist, plus ones a collection job declares but that haven't received data yet (for instance a
        // job still waiting for approval), so a dashboard can be designed ahead of the first run.
        var collected = (await datasets.ListAsync(agentId, ct)).Select(d => d.Name).ToList();
        var declared = CollectionJobService.DeclaredDatasets(await jobs.GetAllAsync(agentId, ct));
        var available = collected.Concat(declared).Distinct().Order().ToList();
        var samples = new Dictionary<string, List<JsonObject>>();

        foreach (var w in spec.Widgets!.Where(w => w.Query is not null))
        {
            var q = w.Query!;
            var name = $"widget '{w.Id}'";
            if (!available.Contains(q.Dataset!))
            {
                errors.Add($"{name}: no dataset '{q.Dataset}'. Available: {(available.Count == 0 ? "none yet" : string.Join(", ", available))}.");
                continue;
            }

            if (!samples.TryGetValue(q.Dataset!, out var sample))
            {
                var rows = await datasets.QueryAsync(agentId, q.Dataset!, null, null, null, true, SampleSize, ct) ?? [];
                samples[q.Dataset!] = sample = [.. rows.Select(r => JsonNode.Parse(r.ValuesJson)).OfType<JsonObject>()];
            }
            if (sample.Count == 0) continue; // nothing collected yet: can't check fields, structure is enough

            var fields = sample.SelectMany(s => s.Select(p => p.Key)).Distinct().Order().ToList();
            bool Known(string f)
            {
                return fields.Contains(f);
            }
            string Available()
            {
                return $"Available fields: {string.Join(", ", fields)}.";
            }

            if (q.Field is not null && !Known(q.Field))
                errors.Add($"{name}: field '{q.Field}' not found in dataset '{q.Dataset}'. {Available()}");
            foreach (var key in (IEnumerable<string>?)q.Filter?.Keys ?? [])
                if (!Known(key)) errors.Add($"{name}: filter key '{key}' not found in dataset '{q.Dataset}'. {Available()}");
            foreach (var column in w.Options?.Columns ?? [])
                if (!Known(column)) errors.Add($"{name}: column '{column}' not found in dataset '{q.Dataset}'. {Available()}");

            if (!string.IsNullOrEmpty(q.Target) && Known("target"))
            {
                var targets = sample.Select(s => s["target"]?.ToString()).OfType<string>().Distinct().ToList();
                if (!targets.Contains(q.Target, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"{name}: no connection named '{q.Target}'. Connections: {string.Join(", ", targets)}.");
            }
        }
        return new DashboardValidation(spec, errors);
    }

    /// <summary>Saves a validated spec as a new version. Returns the version number.</summary>
    public async Task<int> SaveAsync(Guid agentId, DashboardSpec spec, string? reason, Guid? agentRunId, CancellationToken ct)
    {
        return await SaveVersionAsync(agentId, DashboardSpecJson.Serialize(spec), reason, agentRunId, ct);
    }

    public async Task<StoredDashboard?> GetCurrentAsync(Guid agentId, CancellationToken ct)
    {
        return await repo.GetCurrentAsync(agentId, ct);
    }

    /// <summary>The current version (or <paramref name="version"/>) for the UI; null when the agent has no dashboard yet.</summary>
    public async Task<DashboardDto?> Get(Guid agentId, int? version, CancellationToken ct)
    {
        await EnsureDashboardAgent(agentId, ct);
        var stored = await LoadAsync(agentId, version, ct);
        if (stored is null && version is not null) throw new NotFoundException($"Dashboard version {version} not found.");
        return stored is null ? null : new DashboardDto(stored.Version, stored.Reason, stored.UpdatedAt, JsonSerializer.Deserialize<JsonElement>(stored.SpecJson));
    }

    /// <summary>Live data for every widget of a version (default: current).</summary>
    public async Task<DashboardDataDto> GetData(Guid agentId, int? version, CancellationToken ct)
    {
        await EnsureDashboardAgent(agentId, ct);
        var stored = await LoadAsync(agentId, version, ct) ?? throw new NotFoundException("No dashboard yet.");
        var (spec, error) = DashboardSpecJson.Parse(stored.SpecJson);
        if (spec is null) throw new InvalidOperationException($"Stored dashboard version {stored.Version} can't be read: {error}");
        return new DashboardDataDto(DateTimeOffset.UtcNow, await BuildDataAsync(agentId, spec, ct));
    }

    public async Task<List<DashboardVersionDto>> GetVersions(Guid agentId, CancellationToken ct)
    {
        await EnsureDashboardAgent(agentId, ct);
        return await repo.ListVersionsAsync(agentId, ct);
    }

    /// <summary>Makes an old version current again by saving it as a new version.</summary>
    public async Task<DashboardVersionDto> Restore(Guid agentId, int version, CancellationToken ct)
    {
        await EnsureDashboardAgent(agentId, ct);
        var old = await repo.GetVersionAsync(agentId, version, ct) ?? throw new NotFoundException($"Dashboard version {version} not found.");
        var reason = $"Restored v{version}";
        var restored = await SaveVersionAsync(agentId, old.SpecJson, reason, null, ct);
        return new DashboardVersionDto(restored, reason, null, DateTimeOffset.UtcNow);
    }

    public async Task<Dictionary<string, object>> BuildDataAsync(Guid agentId, DashboardSpec spec, CancellationToken ct)
    {
        var freshness = CollectionJobService.FreshnessWindows(await jobs.GetAllAsync(agentId, ct));
        return await new DashboardDataBuilder(datasets, time, freshness).BuildAsync(agentId, spec, ct);
    }

    private async Task<StoredDashboard?> LoadAsync(Guid agentId, int? version, CancellationToken ct)
    {
        return version is { } v ? await repo.GetVersionAsync(agentId, v, ct) : await repo.GetCurrentAsync(agentId, ct);
    }

    private async Task EnsureDashboardAgent(Guid agentId, CancellationToken ct)
    {
        if (!await agents.IsDashboardAlertsAgentAsync(agentId, ct)) throw new NotFoundException($"Agent {agentId} not found.");
    }

    /// <summary>Saves a new version (creating the dashboard on first save), makes it current, and drops versions older
    /// than the newest <see cref="KeepVersions"/>.</summary>
    private async Task<int> SaveVersionAsync(Guid agentId, string specJson, string? reason, Guid? agentRunId, CancellationToken ct)
    {
        var dashboard = await repo.GetAsync(agentId, ct);
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

        var oldest = dashboard.CurrentVersion - KeepVersions;
        if (oldest > 0)
            db.DashboardVersions.RemoveRange(await db.DashboardVersions.Where(v => v.DashboardId == dashboard.Id && v.Version <= oldest).ToListAsync(ct));

        await db.SaveChangesAsync(ct);
        return dashboard.CurrentVersion;
    }
}
