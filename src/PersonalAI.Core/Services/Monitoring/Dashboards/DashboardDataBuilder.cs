using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalAI.Core.Interfaces;

namespace PersonalAI.Core.Services.Monitoring.Dashboards;

public record StatData(double? Value, string? Unit, string? Status, string? Detail, double? Min, double? Max, string? Message);
public record BarItem(string Label, double Value, string? Status);
public record BarData(List<BarItem> Items, string? Unit, double? Max, int Hidden);
public record GridItem(string Label, string Text, string Status);
public record GridData(List<GridItem> Items, int Hidden);
public record TableData(List<string> Columns, List<List<object?>> Rows, int Hidden);
public record SeriesData(string Label, List<double[]> Points);
public record TimeseriesData(List<SeriesData> Series, string? Unit, double? Min, double? Max, double? Warn, double? Crit, bool LowerIsWorse, int HiddenSeries);
public record WidgetError(string Error);
public record EmptyData();

/// <summary>Runs each widget's query against the agent's datasets and shapes the result for the frontend.
/// A failing widget yields <see cref="WidgetError"/> and never fails the whole dashboard.</summary>
public sealed class DashboardDataBuilder(IDatasetRepository datasets, TimeProvider time, IReadOnlyDictionary<string, TimeSpan>? freshness)
{
    public const int MaxSeries = 8, DefaultBarLimit = 12, DefaultTableLimit = 20, FetchLimit = 2000, SeriesPerEntityLimit = 12000;

    /// <summary>Widgets that show "the latest per entity" ignore rows older than this, so a removed container or a
    /// deleted server drops off the dashboard instead of showing its last state indefinitely. This is the floor: a
    /// dataset written less often than every ~20 minutes gets a longer window from <c>freshness</c> (three times its
    /// job's interval), so a daily discovery is not "No data" for 23 hours of every day.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public async Task<Dictionary<string, object>> BuildAsync(Guid agentId, DashboardSpec spec, CancellationToken ct)
    {
        var result = new Dictionary<string, object>();
        foreach (var w in spec.Widgets ?? [])
        {
            if (w.Id is null) continue;
            try
            {
                result[w.Id] = await BuildWidgetAsync(agentId, w, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result[w.Id] = new WidgetError(ex.Message);
            }
        }
        return result;
    }

    private async Task<object> BuildWidgetAsync(Guid agentId, DashboardWidget w, CancellationToken ct)
    {
        if (w.Type == DashboardWidgetTypes.Markdown) return new EmptyData();
        var q = w.Query!;
        var o = w.Options ?? new WidgetOptions(null, null, null, null, null, null, null, null, null);

        if (w.Type == DashboardWidgetTypes.Timeseries)
            return await BuildTimeseriesAsync(agentId, q, o, ct);

        var rows = await LatestAsync(agentId, q, ct);
        if (rows is null) return new WidgetError($"No dataset named '{q.Dataset}'.");

        return w.Type switch
        {
            DashboardWidgetTypes.Stat => BuildStat(rows, q, o, gauge: false),
            DashboardWidgetTypes.Gauge => BuildStat(rows, q, o, gauge: true),
            DashboardWidgetTypes.Bar => BuildBar(rows, q, o),
            DashboardWidgetTypes.StatusGrid => BuildGrid(rows, q, o),
            DashboardWidgetTypes.Table => BuildTable(rows, o),
            _ => new WidgetError($"Unknown widget type '{w.Type}'.")
        };
    }

    // ---- fetching & row helpers -------------------------------------------------

    private async Task<List<MonitoringRow>?> LatestAsync(Guid agentId, WidgetQuery q, CancellationToken ct)
    {
        var raw = await datasets.QueryAsync(agentId, q.Dataset!, null, null, null, true, FetchLimit, ct);
        if (raw is null) return null;
        var window = freshness is not null && freshness.TryGetValue(q.Dataset!, out var w) && w > StaleAfter ? w : StaleAfter;
        var cutoff = time.GetUtcNow().UtcDateTime - window;
        return [.. MonitoringRows.Filter(MonitoringRows.Parse(raw), q.Target, q.Filter).Where(r => r.Ts >= cutoff)];
    }

    internal static string? StatusOf(double value, bool isBool, WidgetOptions o)
    {
        if (isBool) return value >= 1 ? "ok" : "critical";
        if (o.Warn is null && o.Crit is null) return null;

        var lower = o.LowerIsWorse == true;
        bool Trips(double? limit)
        {
            return limit is { } l && (lower ? value <= l : value >= l);
        }
        return Trips(o.Crit) ? "critical" : Trips(o.Warn) ? "warning" : "ok";
    }

    // ---- widgets ----------------------------------------------------------------

    private static StatData BuildStat(List<MonitoringRow> rows, WidgetQuery q, WidgetOptions o, bool gauge)
    {
        var points = rows.Select(r => (Row: r, V: MonitoringRows.Read(r.Values, q.Field!)))
            .Where(p => p.V.Value is not null).ToList();
        var min = gauge ? o.Min ?? 0 : (double?)null;
        var max = gauge ? o.Max ?? 100 : (double?)null;
        if (points.Count == 0) return new StatData(null, o.Unit, null, null, min, max, "No data");

        var isBool = points.All(p => p.V.IsBool);
        var agg = q.Agg ?? "max";
        double value;
        string? detail = null;
        switch (agg)
        {
            case "min": { var p = points.MinBy(x => x.V.Value)!; value = p.V.Value!.Value; detail = p.Row.Label; break; }
            case "latest": { var p = points.MaxBy(x => x.Row.Ts)!; value = p.V.Value!.Value; detail = p.Row.Label; break; }
            case "avg": value = points.Average(p => p.V.Value!.Value); detail = $"average of {points.Count}"; break;
            case "sum": value = points.Sum(p => p.V.Value!.Value); detail = $"sum of {points.Count}"; break;
            case "count": value = points.Count(p => p.V.Value != 0); detail = $"of {points.Count}"; break;
            default: { var p = points.MaxBy(x => x.V.Value)!; value = p.V.Value!.Value; detail = p.Row.Label; break; }
        }
        // Only a picked boolean (max/min/latest) keeps ok/critical semantics; a count, sum or average of
        // booleans is a plain number, so status then comes from thresholds alone.
        var pickedBool = isBool && agg is "max" or "min" or "latest";
        var status = StatusOf(value, pickedBool, o);
        return new StatData(Math.Round(value, 2), o.Unit, status, points.Count > 1 || agg == "count" ? detail : points[0].Row.Label, min, max, null);
    }

    private static BarData BuildBar(List<MonitoringRow> rows, WidgetQuery q, WidgetOptions o)
    {
        var items = rows.Select(r => (r.Label, V: MonitoringRows.Read(r.Values, q.Field!)))
            .Where(p => p.V.Value is not null)
            .Select(p => new BarItem(p.Label, Math.Round(p.V.Value!.Value, 2), StatusOf(p.V.Value.Value, p.V.IsBool, o)))
            .ToList();
        items = o.Sort == "asc" ? [.. items.OrderBy(i => i.Value)] : [.. items.OrderByDescending(i => i.Value)];
        var limit = o.Limit ?? DefaultBarLimit;
        return new BarData([.. items.Take(limit)], o.Unit, o.Max, Math.Max(0, items.Count - limit));
    }

    private static GridData BuildGrid(List<MonitoringRow> rows, WidgetQuery q, WidgetOptions o)
    {
        var items = new List<GridItem>();
        foreach (var r in rows.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase))
        {
            var (v, isBool) = MonitoringRows.Read(r.Values, q.Field!);
            if (v is null) { items.Add(new GridItem(r.Label, "no data", "none")); continue; }
            var status = StatusOf(v.Value, isBool, o) ?? "none";
            var text = isBool ? (v >= 1 ? "OK" : "Down") : $"{Math.Round(v.Value, 1).ToString(CultureInfo.InvariantCulture)}{o.Unit}";
            items.Add(new GridItem(r.Label, text, status));
        }
        var limit = o.Limit ?? 100;
        return new GridData([.. items.Take(limit)], Math.Max(0, items.Count - limit));
    }

    private static TableData BuildTable(List<MonitoringRow> rows, WidgetOptions o)
    {
        var columns = o.Columns ?? [];
        var limit = o.Limit ?? DefaultTableLimit;
        var ordered = rows.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var cells = ordered.Take(limit)
            .Select(r => columns.Select(c => Cell(r.Values[c])).ToList()).ToList();
        return new TableData(columns, cells, Math.Max(0, ordered.Count - limit));
    }

    private static object? Cell(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue<double>(out var d) => d,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString()
        };
    }

    private async Task<object> BuildTimeseriesAsync(Guid agentId, WidgetQuery q, WidgetOptions o, CancellationToken ct)
    {
        var window = q.WindowMinutes ?? 60;
        var bucket = q.BucketMinutes ?? Math.Max(1, (int)Math.Ceiling(window / 120.0));
        var now = time.GetUtcNow().UtcDateTime;
        var from = now.AddMinutes(-window);
        var lower = o.LowerIsWorse == true;

        // First pick which entities to draw, from their newest points (one small query): the worst MaxSeries by the
        // plotted field, then fetch only those entities' windows, each with its own cap. Fetching the whole dataset's
        // window with one global cap silently dropped the oldest points of long windows once the fleet grew.
        var latest = await datasets.QueryAsync(agentId, q.Dataset!, null, null, null, true, FetchLimit, ct);
        if (latest is null) return new WidgetError($"No dataset named '{q.Dataset}'.");

        var candidates = MonitoringRows.Filter(MonitoringRows.Parse(latest), q.Target, q.Filter)
            .Select(r => (Row: r, V: MonitoringRows.Read(r.Values, q.Field!).Value))
            .ToList();
        // Entities whose newest point has no value for the field (a server that just went down) still rank, last,
        // so their history up to the outage stays visible.
        var withValue = lower ? candidates.Where(c => c.V is not null).OrderBy(c => c.V) : candidates.Where(c => c.V is not null).OrderByDescending(c => c.V);
        var ranked = withValue.Concat(candidates.Where(c => c.V is null).OrderBy(c => c.Row.Label, StringComparer.OrdinalIgnoreCase)).ToList();

        var size = TimeSpan.FromMinutes(bucket).Ticks;
        var shown = new List<SeriesData>();
        foreach (var candidate in ranked.Take(MaxSeries))
        {
            var raw = await datasets.QueryAsync(agentId, q.Dataset!, candidate.Row.EntityKey, null, from, false, SeriesPerEntityLimit, ct) ?? [];
            var rows = MonitoringRows.Parse(raw);
            var points = new List<double[]>();
            foreach (var slot in rows.GroupBy(r => r.Ts.Ticks / size).OrderBy(g => g.Key))
            {
                var values = slot.Select(r => MonitoringRows.Read(r.Values, q.Field!).Value).Where(v => v is not null).Select(v => v!.Value).ToList();
                if (values.Count == 0) continue;
                var ms = new DateTimeOffset(new DateTime(slot.Key * size, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                points.Add([ms, Math.Round(values.Average(), 2)]);
            }
            if (points.Count > 0) shown.Add(new SeriesData(rows[^1].Label, points));
        }

        shown = [.. shown.OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)];
        return new TimeseriesData(shown, o.Unit, o.Min, o.Max, o.Warn, o.Crit, lower, Math.Max(0, ranked.Count - MaxSeries));
    }
}
