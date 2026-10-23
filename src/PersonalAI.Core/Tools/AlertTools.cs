using System.Text;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Monitoring.Alerts;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalAI.Core.Tools;

public sealed class AlertRuleSaveTool(IServiceScopeFactory scopes) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "alert_rule_save";

    public string Description =>
        "Creates or updates (by name) an alert rule for this agent. A rule watches one numeric or boolean field of a dataset " +
        "for every matching entity after each collection pass, and fires an in-app alert when the condition holds. " +
        "Rules are evaluated automatically and deterministically; you only define them. Examples (use your own dataset and field names): " +
        "a connection that can't be reached = {name:'Data source down', dataset:'data_source_status', field:'available', operator:'eq', " +
        "threshold:0, severity:'critical'} (the platform writes data_source_status for every connection); sustained load = " +
        "{name:'High CPU', dataset:'cpu', field:'cpuPercent', operator:'gte', threshold:90, forMinutes:10, severity:'warning'}; " +
        "full root disk = {name:'Root disk full', dataset:'disk', field:'usedPercent', operator:'gte', threshold:90, " +
        "filter:{mount:'/'}, severity:'critical'}. Boolean fields read as 1 (true) / 0 (false). " +
        "operator is gt, gte, lt, lte, eq or ne. forMinutes (default 0) is how long the condition must hold before firing; " +
        "use it for noisy metrics like CPU, not for reachability. Use 'target' for one connection (server, database...) and 'filter' to narrow " +
        "(e.g. a mount). Rules are checked against the collected data, so a wrong field name returns the valid ones. " +
        "Keep the set small and meaningful (at most 50); call alert_list first and update rather than duplicating. You can " +
        "only change rules you created, not the user's.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Unique rule name; saving an existing name updates that rule." },
            "description": { "type": "string" },
            "dataset": { "type": "string" },
            "field": { "type": "string", "description": "Numeric or boolean field of the dataset's values." },
            "operator": { "type": "string", "enum": ["gt", "gte", "lt", "lte", "eq", "ne"] },
            "threshold": { "type": "number" },
            "forMinutes": { "type": "integer", "description": "Condition must hold this long before firing (default 0)." },
            "severity": { "type": "string", "enum": ["warning", "critical"] },
            "target": { "type": "string", "description": "Only this connection (by name)." },
            "filter": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Field equality filters, e.g. {\"mount\":\"/\"}." },
            "enabled": { "type": "boolean" }
          },
          "required": ["name", "dataset", "field", "operator", "threshold", "severity"]
        }
        """;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();

        AlertRuleInput? input;
        try
        {
            input = JsonSerializer.Deserialize<AlertRuleInput>(arguments.GetRawText(), Json);
        }
        catch (JsonException ex)
        {
            return new ToolCompleted($"Arguments are not valid: {ex.Message}", IsError: true);
        }
        if (input is null) return new ToolCompleted("Arguments are required.", IsError: true);

        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<AlertRuleService>()
            .UpsertByNameAsync(agentId, input, AlertSources.Llm, ct);
        if (!result.IsValid)
            return new ToolCompleted("Not saved. Fix these and call alert_rule_save again:\n- " + string.Join("\n- ", result.Errors), IsError: true);

        return new ToolCompleted($"{(result.Created ? "Created" : "Updated")} rule '{result.Rule!.Name}': {AlertText.Describe(result.Rule)}.");
    }
}

public sealed class AlertRuleDeleteTool(IServiceScopeFactory scopes) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "alert_rule_delete";

    public string Description =>
        "Deletes one of your own alert rules by name, along with its alert history. Use it to remove rules that are " +
        "redundant or no longer meaningful. You cannot delete the user's rules.";

    public string ParametersSchemaJson => """
        { "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();
        if (arguments.GetStringArg("name") is not { Length: > 0 } name) return new ToolCompleted("'name' is required.", IsError: true);

        await using var scope = scopes.CreateAsyncScope();
        var error = await scope.ServiceProvider.GetRequiredService<AlertRuleService>().DeleteByNameAsync(agentId, name, AlertSources.Llm, ct);
        return error is null ? new ToolCompleted($"Deleted rule '{name}'.") : new ToolCompleted(error, IsError: true);
    }
}

public sealed class AlertListTool(IServiceScopeFactory scopes) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "alert_list";

    public string Description =>
        "Lists this agent's alert rules (with who created each and whether it is enabled) and its alerts: everything " +
        "currently firing, and what resolved in the last 24 hours. Call this before creating rules, and when summarising " +
        "the state of the fleet.";

    public string ParametersSchemaJson => """{ "type": "object", "properties": {} }""";

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();

        await using var scope = scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAlertsRepository>();
        var rules = await repo.GetRulesAsync(agentId, ct);
        var events = await repo.GetEventsAsync(agentId, firing: true, DateTime.UtcNow.AddHours(-24), ct);
        var names = rules.ToDictionary(r => r.Id, r => r.Name);

        var sb = new StringBuilder();
        sb.AppendLine($"Rules ({rules.Count}):");
        if (rules.Count == 0) sb.AppendLine("  none yet");
        foreach (var r in rules)
            sb.AppendLine($"- {r.Name} [{r.Severity}, {r.Source}{(r.Enabled ? "" : ", disabled")}]: {AlertText.Describe(r)}");

        sb.AppendLine();
        var firing = events.Where(e => e.State == AlertState.Firing).ToList();
        sb.AppendLine($"Firing now ({firing.Count}):");
        if (firing.Count == 0) sb.AppendLine("  none");
        foreach (var e in firing)
            sb.AppendLine($"- [{names.GetValueOrDefault(e.RuleId, "?")}] {e.Message} (since {e.FiredAt:u}{(e.Acknowledged ? ", acknowledged" : "")})");

        var resolved = events.Where(e => e.State == AlertState.Resolved).ToList();
        sb.AppendLine();
        sb.AppendLine($"Resolved in the last 24h ({resolved.Count}):");
        foreach (var e in resolved.Take(20))
            sb.AppendLine($"- [{names.GetValueOrDefault(e.RuleId, "?")}] {e.EntityLabel}: fired {e.FiredAt:u}, resolved {e.ResolvedAt:u}");
        return new ToolCompleted(MonitoringToolSupport.Truncate(sb.ToString()));
    }
}

internal static class AlertText
{
    /// <summary>"cpuPercent ≥ 90 for 10 min on system (web-1) where mount=/": one line a person or model can read.</summary>
    public static string Describe(AlertRule r)
    {
        var sb = new StringBuilder($"{r.Dataset}.{r.Field} {AlertOperators.Symbol(r.Operator)} {r.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (r.ForMinutes > 0) sb.Append($" for {r.ForMinutes} min");
        if (!string.IsNullOrEmpty(r.Target)) sb.Append($", on {r.Target}");
        if (!string.IsNullOrEmpty(r.FilterJson) && JsonSerializer.Deserialize<Dictionary<string, string>>(r.FilterJson) is { Count: > 0 } f)
            sb.Append(", where " + string.Join(", ", f.Select(p => $"{p.Key}={p.Value}")));
        return sb.ToString();
    }
}
