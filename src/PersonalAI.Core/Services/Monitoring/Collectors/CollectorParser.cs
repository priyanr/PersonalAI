using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

/// <param name="Rows">Parsed rows (flat objects). Capped at <see cref="CollectorSpecs.MaxRows"/>.</param>
/// <param name="Diagnostic">Why there are no (or fewer) rows, in words a model can act on; null when all is well.</param>
public record ParseOutput(List<JsonObject> Rows, string? Diagnostic);

/// <summary>Turns a request's output into rows according to a <see cref="ParserSpec"/>. Tolerant by design:
/// a line that doesn't fit is skipped (and counted), never fatal, and an empty result carries a diagnostic
/// that shows the first lines of the output so the model can correct its parser.</summary>
public static class CollectorParser
{
    public static ParseOutput Parse(ParserSpec spec, string stdout)
    {
        stdout ??= "";
        var result = spec.Type switch
        {
            "table" => ParseTable(spec, stdout),
            "json" => ParseJson(spec, stdout),
            "kv" => ParseKeyValue(spec, stdout),
            "regex" => ParseRegex(spec, stdout),
            _ => new ParseOutput([], $"Parser type '{spec.Type}' can't be run here.")
        };

        var rows = result.Rows.Count > CollectorSpecs.MaxRows ? result.Rows.Take(CollectorSpecs.MaxRows).ToList() : result.Rows;
        var diagnostic = result.Diagnostic;
        if (result.Rows.Count > CollectorSpecs.MaxRows)
            diagnostic = $"{result.Rows.Count} rows parsed; only the first {CollectorSpecs.MaxRows} are kept. Narrow the request.";
        else if (rows.Count == 0 && diagnostic is null)
            diagnostic = $"No rows parsed. Output started with: {Preview(stdout)}";
        return new ParseOutput(rows, diagnostic);
    }

    // ---- table --------------------------------------------------------------------

    private static ParseOutput ParseTable(ParserSpec spec, string stdout)
    {
        var columns = spec.Columns ?? [];
        var rows = new List<JsonObject>();
        var skippedShort = 0;
        var lines = Lines(stdout).Skip(spec.SkipLines ?? 0);
        foreach (var line in lines)
        {
            var parts = line.Trim().Split((char[]?)null, spec.LastColumnTakesRest == true ? columns.Count : int.MaxValue, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < columns.Count) { skippedShort++; continue; }

            var row = new JsonObject();
            for (var i = 0; i < columns.Count; i++)
                Set(row, columns[i].Name!, parts[i], columns[i].Type);
            rows.Add(row);
        }
        return new ParseOutput(rows, rows.Count == 0 && skippedShort > 0
            ? $"No line had {columns.Count} columns ({skippedShort} too short). Output started with: {Preview(stdout)}"
            : null);
    }

    // ---- key=value ----------------------------------------------------------------

    private static ParseOutput ParseKeyValue(ParserSpec spec, string stdout)
    {
        var types = (spec.Columns ?? []).Where(c => c.Name is not null).ToDictionary(c => c.Name!, c => c.Type, StringComparer.Ordinal);
        var row = new JsonObject();
        foreach (var line in Lines(stdout))
        {
            var idx = line.IndexOfAny(['=', ':']);
            if (idx <= 0) continue;
            var key = Regex.Replace(line[..idx].Trim(), @"[^A-Za-z0-9_]+", "_").Trim('_');
            if (key.Length == 0 || char.IsDigit(key[0])) continue;
            Set(row, key, line[(idx + 1)..].Trim(), types.GetValueOrDefault(key));
        }
        return new ParseOutput(row.Count == 0 ? [] : [row], null);
    }

    // ---- regex --------------------------------------------------------------------

    private static ParseOutput ParseRegex(ParserSpec spec, string stdout)
    {
        var rx = new Regex(spec.Pattern!, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        var names = rx.GetGroupNames().Where(g => !int.TryParse(g, out _)).ToList();
        var types = (spec.Columns ?? []).Where(c => c.Name is not null).ToDictionary(c => c.Name!, c => c.Type, StringComparer.Ordinal);
        var rows = new List<JsonObject>();
        foreach (var line in Lines(stdout))
        {
            Match m;
            try { m = rx.Match(line); }
            catch (RegexMatchTimeoutException) { return new ParseOutput(rows, "The regular expression took too long on a line; simplify it."); }
            if (!m.Success) continue;

            var row = new JsonObject();
            foreach (var name in names)
                if (m.Groups[name].Success) Set(row, name, m.Groups[name].Value, types.GetValueOrDefault(name));
            if (row.Count > 0) rows.Add(row);
        }
        return new ParseOutput(rows, null);
    }

    // ---- json ---------------------------------------------------------------------

    private static ParseOutput ParseJson(ParserSpec spec, string stdout)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(stdout.Trim());
        }
        catch (JsonException)
        {
            return new ParseOutput([], $"The output is not valid JSON. It started with: {Preview(stdout)}");
        }
        if (root is null) return new ParseOutput([], "The output is JSON null.");

        var node = Navigate(root, spec.Path);
        if (node is null) return new ParseOutput([], $"Path '{spec.Path}' was not found in the JSON. Top-level keys: {TopKeys(root)}.");

        var elements = node is JsonArray array ? array.ToList() : [node];
        var rows = new List<JsonObject>();
        foreach (var element in elements)
        {
            if (element is null) continue;
            var row = new JsonObject();
            if (spec.Fields is { Count: > 0 })
            {
                foreach (var (name, field) in spec.Fields)
                    if (Navigate(element, field.Path) is { } value) SetNode(row, name, value, field.Type);
            }
            else if (element is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                    if (value is JsonValue) row[key] = value.DeepClone(); // scalars only; use fields for nested values
            }
            if (row.Count > 0) rows.Add(row);
        }
        return new ParseOutput(rows, rows.Count == 0 ? "The JSON had no rows with the requested fields." : null);
    }

    private static JsonNode? Navigate(JsonNode node, string? path)
    {
        if (string.IsNullOrEmpty(path)) return node;
        var current = node;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null
            };
            if (current is null) return null;
        }
        return current;
    }

    private static string TopKeys(JsonNode root)
    {
        return root switch
        {
            JsonObject o => string.Join(", ", o.Select(p => p.Key).Take(12)),
            JsonArray => "(an array: use an empty path, or a numeric index)",
            _ => "(a scalar)"
        };
    }

    // ---- values -------------------------------------------------------------------

    private static void Set(JsonObject row, string name, string raw, string? type)
    {
        var value = Coerce(raw, type);
        if (value is not null) row[name] = value;
    }

    private static void SetNode(JsonObject row, string name, JsonNode value, string? type)
    {
        if (value is not JsonValue v) { row[name] = value.ToJsonString().Length > 300 ? null : value.ToJsonString(); return; }
        if (type is null or "string")
        {
            row[name] = v.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"');
            return;
        }
        var coerced = Coerce(v.TryGetValue<string>(out var str) ? str : value.ToJsonString(), type);
        if (coerced is not null) row[name] = coerced;
    }

    /// <summary>A typed JSON value, or null when it can't be read as that type (the field is then left out rather
    /// than stored wrong).</summary>
    private static JsonNode? Coerce(string raw, string? type)
    {
        raw = raw.Trim();
        switch (type)
        {
            case "number":
                var cleaned = raw.TrimEnd('%').Replace(",", "");
                return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? JsonValue.Create(d) : null;
            case "bool":
                return raw.ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" or "up" or "ok" or "active" or "running" or "ready" => JsonValue.Create(true),
                    "false" or "no" or "0" or "down" or "failed" or "inactive" or "stopped" => JsonValue.Create(false),
                    _ => null
                };
            default:
                return JsonValue.Create(raw);
        }
    }

    private static IEnumerable<string> Lines(string text)
    {
        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l));
    }

    private static string Preview(string text)
    {
        var first = Lines(text).Take(3).Select(l => l.Length > 120 ? l[..120] + "…" : l);
        var joined = string.Join(" ⏎ ", first);
        return joined.Length == 0 ? "(empty)" : $"\"{joined}\"";
    }
}
