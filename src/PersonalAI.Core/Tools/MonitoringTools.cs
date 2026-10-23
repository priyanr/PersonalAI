using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalAI.Core.Tools;

/// <summary>Shared plumbing for the Dashboard &amp; Alerts tools: they only make sense inside a run of a
/// Dashboard &amp; Alerts agent (ToolExecutionContext.AgentId), and only ever touch that agent's own
/// servers and datasets.</summary>
internal static class MonitoringToolSupport
{
    public const int MaxOutputChars = 14000;

    public static ToolCompleted NotAvailable() =>
        new("This tool is only available to Dashboard & Alerts agents.", IsError: true);

    public static string Truncate(string text) =>
        text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n…[output truncated; narrow the request]";

    /// <summary>Resolves a connection name among the connections this agent is scoped to, across all of its data
    /// sources, never the whole fleet: names are only unique within a group, so a global lookup could return another
    /// agent's connection of the same name. Returns every match (the same name under two of its tools or groups
    /// yields both) and the names the agent can reach, for an error message.</summary>
    public static async Task<(List<Guid> Ids, List<string> MonitoredNames)> ResolveMonitoredTargetAsync(
        IServiceScopeFactory scopes, Guid agentId, string name, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var agent = await scope.ServiceProvider.GetRequiredService<IAgentsRepository>().GetAsync(agentId, ct);
        if (agent is null) return ([], []);

        var config = DashboardAlertsConfig.Parse(agent.TypeConfigJson);
        var registry = scope.ServiceProvider.GetRequiredService<Services.Monitoring.Collectors.DataSourceRegistry>();
        var connections = new List<DataConnection>();
        foreach (var source in registry.All)
            connections.AddRange(await registry.ConnectionsAsync(source, config, ct));
        return (
            [.. connections.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Id)],
            [.. connections.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order()]);
    }
}

public sealed class DataQueryTool(IServiceScopeFactory scopes) : ITool
{
    private const int DefaultLimit = 100, MaxLimit = 500, DefaultWindowMinutes = 60, MaxWindowMinutes = 7 * 24 * 60;

    public bool RequiresAgentRun => true;

    public string Name => "data_query";

    public string Description =>
        "Reads this agent's collected datasets. With no 'dataset', lists the datasets with their field descriptions, point " +
        "counts and last update. With a 'dataset', returns points from the last 'sinceMinutes' (default 60, max 10080), " +
        "oldest first, one line per point: timestamp | entity | values. 'latest': true returns only each entity's newest " +
        "point. 'target' limits to one connection by name. For long windows set 'bucketMinutes' to average numeric fields " +
        "per entity into buckets instead of reading every raw point. Timestamps are UTC.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "dataset": { "type": "string", "description": "Dataset name. Omit to list datasets." },
            "target": { "type": "string", "description": "Only this connection (by name)." },
            "latest": { "type": "boolean", "description": "Only the newest point per entity (default false)." },
            "sinceMinutes": { "type": "integer", "description": "Window size in minutes (default 60, max 10080). Ignored when latest is true." },
            "bucketMinutes": { "type": "integer", "description": "Average numeric fields per entity over buckets of this many minutes." },
            "limit": { "type": "integer", "description": "Max points (default 100, max 500). The newest points are kept when the window has more." }
          }
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();

        await using var scope = scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDatasetRepository>();

        var dataset = arguments.GetStringArg("dataset");
        if (string.IsNullOrWhiteSpace(dataset))
        {
            var list = await repo.ListAsync(agentId, ct);
            if (list.Count == 0) return new ToolCompleted("No datasets yet — nothing has been collected for this agent.");
            return new ToolCompleted(string.Join("\n", list.Select(d =>
                $"- {d.Name} ({d.Kind}, {d.PointCount} points, last {(d.LastTs is { } t ? t.ToString("u") : "never")}): {d.SchemaHint}")));
        }

        List<string>? prefixes = null;
        if (arguments.GetStringArg("target") is { Length: > 0 } targetName)
        {
            var (ids, monitored) = await MonitoringToolSupport.ResolveMonitoredTargetAsync(scopes, agentId, targetName, ct);
            if (ids.Count == 0)
                return new ToolCompleted($"No connection named '{targetName}'. This agent monitors: {string.Join(", ", monitored)}.", IsError: true);
            prefixes = [.. ids.Select(id => id.ToString())];
        }

        var latest = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.True;
        var window = Math.Clamp(arguments.GetIntArg("sinceMinutes") ?? DefaultWindowMinutes, 1, MaxWindowMinutes);
        var limit = Math.Clamp(arguments.GetIntArg("limit") ?? DefaultLimit, 1, MaxLimit);
        var bucket = arguments.GetIntArg("bucketMinutes") ?? 0;
        // Bucketing needs every raw point in the window, so fetch generously and apply the limit to buckets.
        var fetchLimit = bucket > 0 ? 20000 : limit;

        var from = DateTime.UtcNow.AddMinutes(-window);
        IReadOnlyList<DataPointRow>? rows;
        if (prefixes is null)
        {
            rows = await repo.QueryAsync(agentId, dataset, null, null, from, latest, fetchLimit, ct);
        }
        else
        {
            // One query per matching server id (normally one); merged, newest points kept when over the limit.
            var merged = new List<DataPointRow>();
            rows = null;
            foreach (var prefix in prefixes)
            {
                var part = await repo.QueryAsync(agentId, dataset, null, prefix, from, latest, fetchLimit, ct);
                if (part is null) break;
                merged.AddRange(part);
                rows = merged;
            }
            if (rows is not null)
                rows = latest ? [.. merged.OrderBy(r => r.EntityKey)] : [.. merged.OrderBy(r => r.Ts).TakeLast(fetchLimit)];
        }
        if (rows is null) return new ToolCompleted($"No dataset named '{dataset}'. Call data_query without arguments to list datasets.", IsError: true);
        if (rows.Count == 0) return new ToolCompleted("No points in that window.");

        if (bucket > 0 && !latest)
            rows = Bucket(rows, TimeSpan.FromMinutes(bucket)).TakeLast(limit).ToList();

        var text = string.Join("\n", rows.Select(r => $"{r.Ts:yyyy-MM-dd HH:mm:ss} | {r.EntityKey} | {r.ValuesJson}"));
        return new ToolCompleted(MonitoringToolSupport.Truncate(text));
    }

    /// <summary>Averages every numeric field per (entity, time bucket); non-numeric fields keep the bucket's last value.</summary>
    internal static List<DataPointRow> Bucket(IReadOnlyList<DataPointRow> rows, TimeSpan size)
    {
        var result = new List<DataPointRow>();
        foreach (var group in rows.GroupBy(r => (r.EntityKey, Slot: r.Ts.Ticks / size.Ticks)).OrderBy(g => g.Key.Slot))
        {
            var sums = new Dictionary<string, (double Sum, int Count)>();
            var last = new JsonObject();
            foreach (var row in group)
            {
                if (JsonNode.Parse(row.ValuesJson) is not JsonObject obj) continue;
                foreach (var (key, node) in obj)
                {
                    if (node is JsonValue v && v.TryGetValue<double>(out var d))
                    {
                        var (sum, count) = sums.GetValueOrDefault(key);
                        sums[key] = (sum + d, count + 1);
                    }
                    else
                    {
                        last[key] = node?.DeepClone();
                    }
                }
            }
            foreach (var (key, (sum, count)) in sums)
                last[key] = Math.Round(sum / count, 2);
            result.Add(new DataPointRow(group.Key.EntityKey, new DateTime(group.Key.Slot * size.Ticks, DateTimeKind.Utc), last.ToJsonString()));
        }
        return result;
    }
}
