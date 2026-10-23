using System.Text.Json.Nodes;
using PersonalAI.Core.Interfaces;
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
public sealed class DashboardService(IDashboardRepository repo, IDatasetRepository datasets, ICollectionJobsRepository? jobs = null)
{
    private const int SampleSize = 200;

    public async Task<DashboardValidation> ValidateAsync(Guid agentId, string specJson, CancellationToken ct)
    {
        var (spec, parseError) = DashboardSpecJson.Parse(specJson);
        if (spec is null) return new DashboardValidation(null, [parseError!]);

        var errors = DashboardSpecValidator.Validate(spec);
        if (errors.Count > 0) return new DashboardValidation(spec, errors);

        // Datasets that exist, plus ones a collection job declares but that haven't received data yet (for instance a
        // job still waiting for approval), so a dashboard can be designed ahead of the first run.
        var collected = (await datasets.ListAsync(agentId, ct)).Select(d => d.Name).ToList();
        var declared = jobs is null ? new HashSet<string>() : CollectionJobService.DeclaredDatasets(await jobs.GetAllAsync(agentId, ct));
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
            bool Known(string f) => fields.Contains(f);
            string Available() => $"Available fields: {string.Join(", ", fields)}.";

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
    public Task<int> SaveAsync(Guid agentId, DashboardSpec spec, string? reason, Guid? agentRunId, CancellationToken ct) =>
        repo.SaveAsync(agentId, DashboardSpecJson.Serialize(spec), reason, agentRunId, ct);

    public Task<StoredDashboard?> GetCurrentAsync(Guid agentId, CancellationToken ct) => repo.GetCurrentAsync(agentId, ct);

    public Task<StoredDashboard?> GetVersionAsync(Guid agentId, int version, CancellationToken ct) => repo.GetVersionAsync(agentId, version, ct);

    public Task<IReadOnlyList<DashboardVersionInfo>> ListVersionsAsync(Guid agentId, CancellationToken ct) => repo.ListVersionsAsync(agentId, ct);

    /// <summary>Re-saves an old version as the newest. Null if that version doesn't exist.</summary>
    public async Task<int?> RestoreAsync(Guid agentId, int version, CancellationToken ct)
    {
        var old = await repo.GetVersionAsync(agentId, version, ct);
        return old is null ? null : await repo.SaveAsync(agentId, old.SpecJson, $"Restored v{version}", null, ct);
    }

    public async Task<Dictionary<string, object>> BuildDataAsync(Guid agentId, DashboardSpec spec, CancellationToken ct)
    {
        var freshness = jobs is null ? null : CollectionJobService.FreshnessWindows(await jobs.GetAllAsync(agentId, ct));
        return await new DashboardDataBuilder(datasets, null, freshness).BuildAsync(agentId, spec, ct);
    }
}
