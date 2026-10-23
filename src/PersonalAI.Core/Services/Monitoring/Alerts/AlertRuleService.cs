using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Services.Monitoring.Alerts;

public record AlertRuleInput(
    string? Name, string? Description, string? Dataset, string? Field, string? Operator, double? Threshold,
    int? ForMinutes, string? Severity, string? Target, Dictionary<string, string>? Filter, bool? Enabled);

public record AlertRuleSaveResult(AlertRule? Rule, bool Created, IReadOnlyList<string> Errors)
{
    public bool IsValid => Rule is not null && Errors.Count == 0;
}

/// <summary>Creates, updates and deletes alert rules for both the UI (source "user") and the model (source "llm").
/// Rules are validated against the data that has actually been collected, with the alternatives listed in the
/// error, like dashboard specs. A model may only touch rules it authored.</summary>
public sealed class AlertRuleService(IAlertsRepository alerts, IDatasetRepository datasets, ICollectionJobsRepository? jobs = null)
{
    public const int MaxRules = 50, MaxName = 100, MaxFilterKeys = 5, MaxForMinutes = 1440, SampleSize = 200;

    /// <summary>Creates a rule. Errors (including a duplicate name) leave nothing saved.</summary>
    public Task<AlertRuleSaveResult> CreateAsync(Guid agentId, AlertRuleInput input, string source, CancellationToken ct) =>
        SaveAsync(agentId, input, source, existing: null, ct);

    /// <summary>Updates a rule by id (the UI). Null if the rule doesn't exist.</summary>
    public async Task<AlertRuleSaveResult?> UpdateAsync(Guid agentId, Guid ruleId, AlertRuleInput input, CancellationToken ct)
    {
        var rule = await alerts.GetRuleAsync(agentId, ruleId, ct);
        return rule is null ? null : await SaveAsync(agentId, input, rule.Source, rule, ct);
    }

    /// <summary>Create-or-update by name, as the model does. Refuses to touch a rule the user wrote.</summary>
    public async Task<AlertRuleSaveResult> UpsertByNameAsync(Guid agentId, AlertRuleInput input, string source, CancellationToken ct)
    {
        var existing = string.IsNullOrWhiteSpace(input.Name) ? null : await alerts.GetRuleByNameAsync(agentId, input.Name.Trim(), ct);
        if (existing is not null && existing.Source != source)
            return new AlertRuleSaveResult(null, false, [$"Rule '{existing.Name}' was created by the user; you can't change it. Choose a different name."]);
        return await SaveAsync(agentId, input, source, existing, ct);
    }

    /// <summary>Deletes by name. Returns an error string, or null on success. A model may only delete its own rules.</summary>
    public async Task<string?> DeleteByNameAsync(Guid agentId, string name, string source, CancellationToken ct)
    {
        var rule = await alerts.GetRuleByNameAsync(agentId, name, ct);
        if (rule is null) return $"No rule named '{name}'.";
        if (rule.Source != source) return $"Rule '{rule.Name}' was created by the user; you can't delete it.";
        await alerts.DeleteRuleAsync(agentId, rule.Id, ct);
        return null;
    }

    private async Task<AlertRuleSaveResult> SaveAsync(Guid agentId, AlertRuleInput input, string source, AlertRule? existing, CancellationToken ct)
    {
        var errors = await ValidateAsync(agentId, input, existing, ct);
        if (errors.Count > 0) return new AlertRuleSaveResult(null, false, errors);

        var filterJson = input.Filter is { Count: > 0 } ? JsonSerializer.Serialize(input.Filter) : null;
        var target = string.IsNullOrWhiteSpace(input.Target) ? null : input.Target.Trim();
        var enabled = input.Enabled ?? existing?.Enabled ?? true;

        if (existing is null)
        {
            var created = await alerts.AddRuleAsync(new AlertRule
            {
                AgentId = agentId, Name = input.Name!.Trim(), Description = Clean(input.Description),
                Dataset = input.Dataset!.Trim(), Field = input.Field!.Trim(), Operator = input.Operator!,
                Threshold = input.Threshold!.Value, ForMinutes = input.ForMinutes ?? 0,
                Severity = input.Severity ?? AlertSeverity.Warning, Target = target, FilterJson = filterJson,
                Enabled = enabled, Source = source
            }, ct);
            return new AlertRuleSaveResult(created, true, []);
        }

        var wasEnabled = existing.Enabled;
        existing.Name = input.Name!.Trim();
        // What the rule watches changing (dataset, field, connection or filter) makes its open alerts meaningless: the
        // evaluator would never visit those entities under the new definition and they would stay firing forever.
        // A changed threshold or operator is left to the next evaluation, which updates or resolves them naturally.
        var scopeChanged = existing.Dataset != input.Dataset!.Trim() || existing.Field != input.Field!.Trim()
            || existing.Target != target || existing.FilterJson != filterJson;

        existing.Description = Clean(input.Description);
        existing.Dataset = input.Dataset!.Trim();
        existing.Field = input.Field!.Trim();
        existing.Operator = input.Operator!;
        existing.Threshold = input.Threshold!.Value;
        existing.ForMinutes = input.ForMinutes ?? 0;
        existing.Severity = input.Severity ?? AlertSeverity.Warning;
        existing.Target = target;
        existing.FilterJson = filterJson;
        existing.Enabled = enabled;
        await alerts.SaveRuleAsync(existing, ct);

        // Switching a rule off closes whatever it had open instead of leaving alerts stuck firing.
        if (wasEnabled && !enabled)
            await alerts.ResolveOpenEventsAsync(existing.Id, DateTime.UtcNow, "rule disabled", ct);
        else if (scopeChanged)
            await alerts.ResolveOpenEventsAsync(existing.Id, DateTime.UtcNow, "rule changed", ct);
        return new AlertRuleSaveResult(existing, false, []);
    }

    private async Task<List<string>> ValidateAsync(Guid agentId, AlertRuleInput i, AlertRule? existing, CancellationToken ct)
    {
        var errors = new List<string>();

        var name = i.Name?.Trim();
        if (string.IsNullOrEmpty(name)) errors.Add("name is required.");
        else if (name.Length > MaxName) errors.Add($"name must be at most {MaxName} characters.");
        else
        {
            var clash = await alerts.GetRuleByNameAsync(agentId, name, ct);
            if (clash is not null && clash.Id != existing?.Id) errors.Add($"a rule named '{clash.Name}' already exists.");
        }
        if (existing is null && (await alerts.GetRulesAsync(agentId, ct)).Count >= MaxRules)
            errors.Add($"at most {MaxRules} rules are allowed; delete one first.");

        if (string.IsNullOrWhiteSpace(i.Dataset)) errors.Add("dataset is required.");
        if (string.IsNullOrWhiteSpace(i.Field)) errors.Add("field is required.");
        if (i.Operator is null || !AlertOperators.All.Contains(i.Operator))
            errors.Add($"operator must be one of {string.Join(", ", AlertOperators.All.Order())}.");
        if (i.Threshold is null || double.IsNaN(i.Threshold.Value) || double.IsInfinity(i.Threshold.Value))
            errors.Add("threshold is required and must be a finite number.");
        if (i.ForMinutes is < 0 or > MaxForMinutes) errors.Add($"forMinutes must be between 0 and {MaxForMinutes}.");
        if (i.Severity is not null && !AlertSeverity.All.Contains(i.Severity)) errors.Add("severity must be warning or critical.");
        if (i.Filter is { Count: > MaxFilterKeys }) errors.Add($"filter may have at most {MaxFilterKeys} keys.");
        if (i.Description is { Length: > 500 }) errors.Add("description must be at most 500 characters.");
        if (errors.Count > 0 || string.IsNullOrWhiteSpace(i.Dataset) || string.IsNullOrWhiteSpace(i.Field)) return errors;

        // Check against what has actually been collected.
        // Collected datasets plus ones a collection job declares but that have no data yet.
        var collected = (await datasets.ListAsync(agentId, ct)).Select(d => d.Name);
        var declared = jobs is null ? new HashSet<string>() : CollectionJobService.DeclaredDatasets(await jobs.GetAllAsync(agentId, ct));
        var names = collected.Concat(declared).Distinct().Order().ToList();
        if (names.Count == 0) return errors; // nothing collected or declared yet, so there is nothing to check against
        var dataset = i.Dataset.Trim();
        if (!names.Contains(dataset))
        {
            errors.Add($"no dataset '{dataset}'. Available: {(names.Count == 0 ? "none yet" : string.Join(", ", names))}.");
            return errors;
        }

        var sample = MonitoringRows.Parse(await datasets.QueryAsync(agentId, dataset, null, null, null, true, SampleSize, ct) ?? []);
        if (sample.Count == 0) return errors; // nothing collected yet: can't check fields

        var numeric = sample.SelectMany(r => r.Values.Where(p => IsNumeric(p.Value)).Select(p => p.Key)).Distinct().Order().ToList();
        var field = i.Field.Trim();
        if (!numeric.Contains(field))
            errors.Add($"field '{field}' is not a numeric or boolean field of dataset '{dataset}'. Numeric fields: {string.Join(", ", numeric)}.");

        var all = sample.SelectMany(r => r.Values.Select(p => p.Key)).Distinct().Order().ToList();
        foreach (var key in (IEnumerable<string>?)i.Filter?.Keys ?? [])
            if (!all.Contains(key)) errors.Add($"filter key '{key}' not found in dataset '{dataset}'. Fields: {string.Join(", ", all)}.");

        if (!string.IsNullOrWhiteSpace(i.Target) && all.Contains("target"))
        {
            var targets = sample.Select(r => r.Values["target"]?.ToString()).OfType<string>().Distinct().ToList();
            if (!targets.Contains(i.Target.Trim(), StringComparer.OrdinalIgnoreCase))
                errors.Add($"no connection named '{i.Target.Trim()}'. Connections: {string.Join(", ", targets)}.");
        }
        return errors;
    }

    private static bool IsNumeric(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<double>(out _) || v.TryGetValue<bool>(out _));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
