using System.Text.RegularExpressions;

namespace PersonalAI.Core.Services.Monitoring.Dashboards;

/// <summary>Structural validation of a spec. Dataset/field existence is checked separately against real data
/// (DashboardService), because it needs the agent's datasets.</summary>
public static partial class DashboardSpecValidator
{
    public const int MaxWidgets = 30, MaxTitle = 120, MaxText = 4000, MaxColumns = 8;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex IdPattern();

    public static List<string> Validate(DashboardSpec spec)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(spec.Title)) errors.Add("title is required.");
        else if (spec.Title.Length > MaxTitle) errors.Add($"title must be at most {MaxTitle} characters.");
        if (spec.RefreshSeconds is < 10 or > 300) errors.Add("refreshSeconds must be between 10 and 300.");

        var widgets = spec.Widgets ?? [];
        if (widgets.Count == 0) errors.Add("widgets must contain at least one widget.");
        if (widgets.Count > MaxWidgets) errors.Add($"at most {MaxWidgets} widgets are allowed.");

        var seen = new HashSet<string>();
        for (var i = 0; i < Math.Min(widgets.Count, MaxWidgets); i++)
        {
            var w = widgets[i];
            var name = string.IsNullOrEmpty(w.Id) ? $"widgets[{i}]" : $"widget '{w.Id}'";

            if (string.IsNullOrEmpty(w.Id) || !IdPattern().IsMatch(w.Id))
                errors.Add($"widgets[{i}].id must be 1-40 chars of a-z, 0-9, '-' or '_'.");
            else if (!seen.Add(w.Id))
                errors.Add($"{name}: duplicate id.");

            if (w.Type is null || !DashboardWidgetTypes.All.Contains(w.Type))
            {
                errors.Add($"{name}: type must be one of {string.Join(", ", DashboardWidgetTypes.All.Order())}.");
                continue;
            }
            if (w.Title is { Length: > MaxTitle }) errors.Add($"{name}: title must be at most {MaxTitle} characters.");
            if (w.Span is < 1 or > 3) errors.Add($"{name}: span must be 1, 2 or 3 (columns of a 3-column grid).");

            if (w.Type == DashboardWidgetTypes.Markdown)
            {
                if (string.IsNullOrWhiteSpace(w.Text)) errors.Add($"{name}: markdown widgets need 'text'.");
                else if (w.Text.Length > MaxText) errors.Add($"{name}: text must be at most {MaxText} characters.");
                if (w.Query is not null) errors.Add($"{name}: markdown widgets take no 'query'.");
                continue;
            }

            var q = w.Query;
            if (q is null || string.IsNullOrWhiteSpace(q.Dataset))
            {
                errors.Add($"{name}: query.dataset is required.");
                continue;
            }
            if (DashboardWidgetTypes.NeedField.Contains(w.Type) && string.IsNullOrWhiteSpace(q.Field))
                errors.Add($"{name}: query.field is required for {w.Type} widgets.");
            if (q.Agg is not null && !DashboardWidgetTypes.Aggs.Contains(q.Agg))
                errors.Add($"{name}: query.agg must be one of {string.Join(", ", DashboardWidgetTypes.Aggs.Order())}.");
            if (q.WindowMinutes is < 1 or > 7 * 24 * 60) errors.Add($"{name}: query.windowMinutes must be between 1 and 10080.");
            if (q.BucketMinutes is < 1 or > 24 * 60) errors.Add($"{name}: query.bucketMinutes must be between 1 and 1440.");
            if (w.Text is not null) errors.Add($"{name}: 'text' is only for markdown widgets.");

            var o = w.Options;
            if (w.Type == DashboardWidgetTypes.Table)
            {
                if (o?.Columns is not { Count: > 0 }) errors.Add($"{name}: table widgets need options.columns (value fields to show).");
                else if (o.Columns.Count > MaxColumns) errors.Add($"{name}: at most {MaxColumns} columns.");
            }
            if (o is { Warn: not null, Crit: not null } && o.LowerIsWorse != true && o.Warn > o.Crit)
                errors.Add($"{name}: options.warn must not exceed options.crit (or set lowerIsWorse).");
            if (o is { Warn: not null, Crit: not null } && o.LowerIsWorse == true && o.Warn < o.Crit)
                errors.Add($"{name}: with lowerIsWorse, options.warn must not be below options.crit.");
            if (o is { Min: not null, Max: not null } && o.Min >= o.Max) errors.Add($"{name}: options.min must be below options.max.");
            if (o?.Limit is < 1 or > 100) errors.Add($"{name}: options.limit must be between 1 and 100.");
            if (o?.Sort is not null and not ("asc" or "desc")) errors.Add($"{name}: options.sort must be asc or desc.");
        }
        return errors;
    }
}
