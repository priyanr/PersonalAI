using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAI.Core.Services.Monitoring.Dashboards;

/// <summary>The declarative dashboard a model designs: a list of widgets bound to queries over the agent's
/// datasets. Never markup or script. The server runs each widget's query and shapes the result; the
/// frontend only draws it, so a dashboard stays live between model runs.</summary>
public record DashboardSpec(string? Title, string? Description, int? RefreshSeconds, List<DashboardWidget>? Widgets);

public record DashboardWidget(string? Id, string? Type, string? Title, int? Span, WidgetQuery? Query, WidgetOptions? Options, string? Text);

/// <param name="Dataset">Dataset name (see data_query with no arguments).</param>
/// <param name="Field">Numeric (or boolean) field of the dataset's values to plot or aggregate.</param>
/// <param name="Target">Limit to one connection (server, database...) by name.</param>
/// <param name="Filter">Equality filters on value fields, e.g. {"mount":"/"}.</param>
/// <param name="Agg">latest|avg|min|max|sum|count across the matching entities' newest points (stat, gauge).</param>
/// <param name="WindowMinutes">Timeseries window.</param>
/// <param name="BucketMinutes">Timeseries averaging bucket (auto if omitted).</param>
public record WidgetQuery(string? Dataset, string? Field, string? Target, Dictionary<string, string>? Filter, string? Agg, int? WindowMinutes, int? BucketMinutes);

/// <param name="Warn">Value at which status becomes warning (numeric fields).</param>
/// <param name="Crit">Value at which status becomes critical.</param>
/// <param name="LowerIsWorse">Thresholds trip when the value falls to or below them (e.g. free space).</param>
public record WidgetOptions(string? Unit, double? Min, double? Max, double? Warn, double? Crit, bool? LowerIsWorse, List<string>? Columns, int? Limit, string? Sort);

public static class DashboardWidgetTypes
{
    public const string Stat = "stat", Gauge = "gauge", Timeseries = "timeseries", Bar = "bar",
        Table = "table", StatusGrid = "statusGrid", Markdown = "markdown";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Stat, Gauge, Timeseries, Bar, Table, StatusGrid, Markdown };

    public static readonly IReadOnlySet<string> NeedField = new HashSet<string>(StringComparer.Ordinal)
        { Stat, Gauge, Timeseries, Bar, StatusGrid };

    public static readonly IReadOnlySet<string> Aggs = new HashSet<string>(StringComparer.Ordinal)
        { "latest", "avg", "min", "max", "sum", "count" };
}

public static class DashboardSpecJson
{
    /// <summary>Camel-case, case-insensitive on read, and unknown properties are an error so a model's typo
    /// ("windowMins") is reported instead of silently ignored.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static (DashboardSpec? Spec, string? Error) Parse(string json)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<DashboardSpec>(json, Options);
            return spec is null ? (null, "Spec is empty.") : (spec, null);
        }
        catch (JsonException ex)
        {
            return (null, $"Spec is not valid: {ex.Message}");
        }
    }

    public static string Serialize(DashboardSpec spec)
    {
        return JsonSerializer.Serialize(spec, Options);
    }
}
