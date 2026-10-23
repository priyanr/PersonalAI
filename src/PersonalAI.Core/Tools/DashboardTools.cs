using System.Text;
using System.Text.Json;
using PersonalAI.Core.Services.Monitoring.Dashboards;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalAI.Core.Tools;

internal static class DashboardToolSupport
{
    /// <summary>A short, model-readable digest of what each widget currently shows, so it can check its work.</summary>
    public static async Task<string> PreviewAsync(DashboardService service, Guid agentId, DashboardSpec spec, CancellationToken ct)
    {
        var data = await service.BuildDataAsync(agentId, spec, ct);
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return MonitoringToolSupport.Truncate(json);
    }
}

public sealed class DashboardGetTool(IServiceScopeFactory scopes) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "dashboard_get";

    public string Description =>
        "Returns this agent's current dashboard spec (and its version) plus a preview of the data each widget currently " +
        "shows, so you can refine it instead of starting over. If no dashboard exists yet it says so. Call this before " +
        "dashboard_save on every run.";

    public string ParametersSchemaJson => """{ "type": "object", "properties": {} }""";

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();

        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DashboardService>();
        var current = await service.GetCurrentAsync(agentId, ct);
        if (current is null) return new ToolCompleted("No dashboard exists yet. Build one with dashboard_save.");

        var (spec, error) = DashboardSpecJson.Parse(current.SpecJson);
        if (spec is null) return new ToolCompleted($"The stored dashboard could not be read ({error}). Save a new one.", IsError: true);

        var sb = new StringBuilder()
            .AppendLine($"Version {current.Version} (saved {current.UpdatedAt:u}{(current.Reason is null ? "" : $": {current.Reason}")})")
            .AppendLine(current.SpecJson)
            .AppendLine()
            .AppendLine("Current widget data:")
            .Append(await DashboardToolSupport.PreviewAsync(service, agentId, spec, ct));
        return new ToolCompleted(sb.ToString());
    }
}

public sealed class DashboardSaveTool(IServiceScopeFactory scopes) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "dashboard_save";

    public string Description =>
        "Saves this agent's single dashboard as a new version (previous versions are kept). The spec is declarative JSON: " +
        "a title and a list of widgets, each bound to a query over this agent's datasets. The platform runs the queries on " +
        "every page load, so the dashboard stays live; you are designing it, not snapshotting numbers. The spec is " +
        "validated against the real datasets and fields; on errors nothing is saved and you get the exact problems to fix.\n\n" +
        "Widget types: stat (one headline number), gauge (one value against min/max with status), timeseries (a line per " +
        "connection/entity over windowMinutes), bar (latest value per entity, ranked), statusGrid (one tile per entity with " +
        "ok/warning/critical status), table (latest row per entity showing options.columns), markdown (your own text, " +
        "static until the next run: say 'as of <time>').\n\n" +
        "query: {dataset, field, target?, filter?, agg?, windowMinutes?, bucketMinutes?}. agg (stat/gauge) is how the " +
        "newest point of every matching entity combines: max (default), min, avg, sum, latest, count (entities with a " +
        "non-zero/true value). Boolean fields (available, running) read as ok/critical automatically. " +
        "options: {unit, min, max, warn, crit, lowerIsWorse, columns, limit, sort}. Set warn/crit so status colors mean " +
        "something (e.g. cpuPercent warn 70 crit 90; for free space use lowerIsWorse).\n\n" +
        "Layout: a 3-column grid; span 1-3. Lead with a markdown summary or the statusGrid of data source availability, then " +
        "headline stats/gauges, then timeseries, then tables. Keep it to what matters (typically 6-14 widgets); one chart " +
        "per question; never put two different units on one chart.\n\n" +
        "Example spec: {\"title\":\"Fleet health\",\"widgets\":[" +
        "{\"id\":\"up\",\"type\":\"statusGrid\",\"title\":\"Data sources\",\"span\":3,\"query\":{\"dataset\":\"data_source_status\",\"field\":\"available\"}}," +
        "{\"id\":\"cpu\",\"type\":\"timeseries\",\"title\":\"CPU %\",\"span\":2,\"query\":{\"dataset\":\"cpu\",\"field\":\"cpuPercent\",\"windowMinutes\":360}," +
        "\"options\":{\"unit\":\"%\",\"min\":0,\"max\":100,\"warn\":70,\"crit\":90}}," +
        "{\"id\":\"disk\",\"type\":\"bar\",\"title\":\"Disk used\",\"query\":{\"dataset\":\"disk\",\"field\":\"usedPercent\"},\"options\":{\"unit\":\"%\",\"max\":100,\"warn\":80,\"crit\":90}}]}";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "spec": {
              "type": "object",
              "description": "The dashboard spec.",
              "properties": {
                "title": { "type": "string" },
                "description": { "type": "string" },
                "refreshSeconds": { "type": "integer", "description": "How often the page refetches data (10-300, default 30)." },
                "widgets": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string", "description": "Unique, a-z 0-9 - _ ." },
                      "type": { "type": "string", "enum": ["stat", "gauge", "timeseries", "bar", "statusGrid", "table", "markdown"] },
                      "title": { "type": "string" },
                      "span": { "type": "integer", "description": "Columns of the 3-column grid: 1, 2 or 3." },
                      "text": { "type": "string", "description": "Markdown widgets only." },
                      "query": {
                        "type": "object",
                        "properties": {
                          "dataset": { "type": "string" },
                          "field": { "type": "string" },
                          "target": { "type": "string", "description": "Only this connection." },
                          "filter": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Field equality filters, e.g. {\"mount\":\"/\"}." },
                          "agg": { "type": "string", "enum": ["latest", "avg", "min", "max", "sum", "count"] },
                          "windowMinutes": { "type": "integer" },
                          "bucketMinutes": { "type": "integer" }
                        }
                      },
                      "options": {
                        "type": "object",
                        "properties": {
                          "unit": { "type": "string" },
                          "min": { "type": "number" },
                          "max": { "type": "number" },
                          "warn": { "type": "number" },
                          "crit": { "type": "number" },
                          "lowerIsWorse": { "type": "boolean" },
                          "columns": { "type": "array", "items": { "type": "string" } },
                          "limit": { "type": "integer" },
                          "sort": { "type": "string", "enum": ["asc", "desc"] }
                        }
                      }
                    },
                    "required": ["id", "type"]
                  }
                }
              },
              "required": ["title", "widgets"]
            },
            "reason": { "type": "string", "description": "One sentence on what changed and why (shown in version history)." }
          },
          "required": ["spec", "reason"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();

        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("spec", out var specElement) || specElement.ValueKind != JsonValueKind.Object)
            return new ToolCompleted("'spec' (an object) is required.", IsError: true);

        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DashboardService>();

        var validation = await service.ValidateAsync(agentId, specElement.GetRawText(), ct);
        if (!validation.IsValid)
            return new ToolCompleted("Not saved. Fix these and call dashboard_save again:\n- " + string.Join("\n- ", validation.Errors), IsError: true);

        Guid? runId = context.Sandbox is { Kind: SandboxOwnerKind.AgentRun } owner ? owner.Id : null;
        var version = await service.SaveAsync(agentId, validation.Spec!, arguments.GetStringArg("reason"), runId, ct);
        var preview = await DashboardToolSupport.PreviewAsync(service, agentId, validation.Spec!, ct);
        return new ToolCompleted($"Saved dashboard version {version}. Current widget data (check for errors or empty widgets):\n{preview}");
    }
}
