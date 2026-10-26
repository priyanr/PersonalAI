using System.Text.Json;
using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Services.Agents.Types;

public sealed class DashboardAlertsAgentTypeDescriptor : IAgentTypeDescriptor
{
    public AgentType Type => AgentType.DashboardAlerts;
    public string DisplayName => "Dashboard & Alerts";
    public string Description => "Runs collection jobs (designed by the model, approved by you) on your data sources (servers, databases...) on an interval and builds a dashboard and alert rules from the data.";

    public string? SystemPromptPreamble =>
        "You are a monitoring analyst for the data sources assigned to this agent (servers, databases, anything you have a data tool for). You decide what is collected, how it is shown and " +
        "when it alerts; the platform then runs your work on a schedule with no model involved. Follow the instructions below " +
        "for what to monitor.\n\n" +

        "HOW DATA GETS COLLECTED: collection jobs. A job sends one read-only request through a data tool to its connections on an interval, parses the " +
        "answer into rows and appends them to a dataset. Dashboards and alert rules read datasets. You design jobs; you do not " +
        "collect data yourself.\n" +
        "1. Call collector_list and data_query (no arguments) to see the jobs and datasets that already exist. Do not recreate " +
        "what exists. The platform itself writes the dataset data_source_status (available true/false per connection, for any " +
        "tool), so you never collect reachability yourself: alert on it.\n" +
        "2. To monitor something new, explore with your data tools (for example ssh for servers): one read-only request at a time. A refused " +
        "request explains what is allowed. A request that is merely not on the tool's allowed list can still go in a collection job " +
        "(it cannot be tried first): the user is then asked to approve that exact request. Prefer requests with structured output " +
        "(JSON, one value per line, or plain columns).\n" +
        "3. Design the job and check it with collector_test until the sample rows have the right fields and types (numbers as " +
        "number, flags as bool). Use kind snapshot for current state (pods, containers), timeseries for numeric metrics over " +
        "time, and keyFields to identify one entity per connection (for example namespace and name for pods).\n" +
        "4. Save it with collector_save. It is validated and dry-run first. Unless the user turned approval off, it then waits " +
        "for their approval and collects nothing until they give it: say so in your final reply, naming the job. You can still " +
        "build dashboards and alert rules on its dataset meanwhile.\n" +
        "Keep jobs cheap: they run every interval on every connection. 5 minutes is the default; use longer for heavy requests. " +
        "Never invent values; base everything on output you have actually seen.\n\n" +

        "DASHBOARD: call data_query to read recent data and dashboard_get to see the current dashboard, then dashboard_save. " +
        "Refine the existing dashboard rather than rewriting it, keep it focused, and read the widget data dashboard_save " +
        "returns to confirm nothing is empty or broken.\n\n" +

        "ALERTS: rules are evaluated automatically after every collection and fire in-app alerts; you only define them. Call " +
        "alert_list first (rules, what is firing, what recently resolved), then alert_rule_save for what is missing and " +
        "alert_rule_delete for your own redundant rules. Use sensible thresholds based on the data you have read; do not create " +
        "noisy rules. You cannot change rules the user wrote.\n\n" +

        "Your final reply should be a short summary: the state of what is monitored, anything firing, jobs waiting for approval, and " +
        "what you changed.";

    /// <summary>The generic tools: nothing here names a particular data source. The data-source tools (ssh, and later
    /// others) come from the agent's scopes; see <see cref="ToolsFor"/>.</summary>
    public IReadOnlyList<string> RequiredTools =>
    [
        "collector_test", "collector_save", "collector_list", "collector_delete",
        "data_query", "dashboard_get", "dashboard_save", "alert_rule_save", "alert_rule_delete", "alert_list"
    ];

    public IReadOnlyList<string> ToolsFor(string? typeConfigJson)
    {
        var scopes = DashboardAlertsConfig.Parse(typeConfigJson).ToolScopes;
        return [.. RequiredTools, .. scopes.Where(p => p.Value.Count > 0).Select(p => p.Key).Order(StringComparer.OrdinalIgnoreCase)];
    }

    public (string? NormalizedJson, string? Error) NormalizeConfig(string? typeConfigJson)
    {
        DashboardAlertsConfig config;
        try
        {
            config = DashboardAlertsConfig.ParseStrict(typeConfigJson);
        }
        catch (JsonException ex)
        {
            return (null, $"TypeConfig is not valid: {ex.Message}");
        }

        if (config.CollectIntervalMinutes is < DashboardAlertsConfig.MinCollectIntervalMinutes or > DashboardAlertsConfig.MaxCollectIntervalMinutes)
            return (null, $"Collect interval must be between {DashboardAlertsConfig.MinCollectIntervalMinutes} and {DashboardAlertsConfig.MaxCollectIntervalMinutes} minutes.");
        if (config.RetentionDays < 1) return (null, "Retention must be at least 1 day.");

        // Keep only tools that have something selected, with lowercase names and no duplicate ids.
        var scopes = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tool, ids) in config.ToolScopes)
        {
            var name = tool.Trim().ToLowerInvariant();
            if (name.Length == 0 || ids.Count == 0) continue;
            if (ids.Count > 200) return (null, $"Tool '{tool}' has too many scope items selected.");
            scopes[name] = [.. ids.Distinct()];
        }
        config = config with { ToolScopes = scopes };

        return (JsonSerializer.Serialize(config, DashboardAlertsConfig.JsonOptions), null);
    }
}
