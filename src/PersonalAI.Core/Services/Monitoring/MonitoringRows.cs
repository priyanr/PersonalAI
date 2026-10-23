using System.Text.Json.Nodes;
using PersonalAI.Core.Interfaces;

namespace PersonalAI.Core.Services.Monitoring;

/// <summary>One data point with its values parsed, plus the helpers every consumer of collected data (dashboard
/// widgets, alert rules) shares, so a filter or field means exactly the same thing everywhere.</summary>
public sealed record MonitoringRow(string EntityKey, DateTime Ts, JsonObject Values)
{
    private static readonly string[] SubKeys = ["mount", "sensor", "name"];

    /// <summary>"connection" or "connection · key": what a person calls this entity.</summary>
    public string Label
    {
        get
        {
            var target = Values["target"]?.ToString();
            foreach (var sub in SubKeys)
                if (Values[sub]?.ToString() is { Length: > 0 } s) return target is null ? s : $"{target} · {s}";
            return target ?? EntityKey;
        }
    }
}

public static class MonitoringRows
{
    public static List<MonitoringRow> Parse(IEnumerable<DataPointRow> raw)
    {
        var rows = new List<MonitoringRow>();
        foreach (var r in raw)
            if (JsonNode.Parse(r.ValuesJson) is JsonObject obj)
                rows.Add(new MonitoringRow(r.EntityKey, r.Ts, obj));
        return rows;
    }

    /// <summary>Keeps rows for the named connection (case-insensitive) whose values equal every filter entry.</summary>
    public static List<MonitoringRow> Filter(IEnumerable<MonitoringRow> rows, string? target, IReadOnlyDictionary<string, string>? filter)
    {
        var result = rows;
        if (!string.IsNullOrEmpty(target))
            result = result.Where(r => string.Equals(r.Values["target"]?.ToString(), target, StringComparison.OrdinalIgnoreCase));
        foreach (var (key, value) in filter ?? new Dictionary<string, string>())
            result = result.Where(r => string.Equals(r.Values[key]?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        return [.. result];
    }

    /// <summary>A numeric field value; booleans read as 1/0 and report IsBool.</summary>
    public static (double? Value, bool IsBool) Read(JsonObject values, string field)
    {
        if (values[field] is not JsonValue v) return (null, false);
        if (v.TryGetValue<double>(out var d)) return (d, false);
        if (v.TryGetValue<bool>(out var b)) return (b ? 1 : 0, true);
        return (null, false);
    }
}
