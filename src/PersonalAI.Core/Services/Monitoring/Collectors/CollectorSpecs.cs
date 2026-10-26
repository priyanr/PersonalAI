using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

/// <param name="Name">Column name; for table parsers the position in the list is the column position.</param>
/// <param name="Type">string (default), number or bool.</param>
public record ColumnSpec(string? Name, string? Type);

/// <param name="Path">Dotted path inside one row element, with numeric array indexes: "status.containerStatuses.0.restartCount".</param>
public record FieldSpec(string? Path, string? Type);

/// <summary>How a request's output becomes rows. <c>table</c>: whitespace-separated columns (the last may take the
/// rest of the line). <c>json</c>: a dotted <c>path</c> to an array (a row per element) or an object (one row), with
/// <c>fields</c> mapping names to paths. <c>kv</c>: "key=value" / "key: value" lines into one row. <c>regex</c>: named
/// groups per line.</summary>
public record ParserSpec(
    string? Type, int? SkipLines, List<ColumnSpec>? Columns, bool? LastColumnTakesRest,
    string? Path, Dictionary<string, FieldSpec>? Fields, string? Pattern);

/// <param name="Name">Dataset name (lowercase letters, digits, '_' or '-').</param>
/// <param name="Kind">timeseries (default), snapshot or event.</param>
/// <param name="KeyFields">Row fields that identify an entity within a connection (e.g. ["namespace","name"] for pods).
/// None means one entity per connection.</param>
public record DatasetSpec(string? Name, string? Kind, List<string>? KeyFields);

public static partial class CollectorSpecs
{
    public const int MaxRows = 500, MaxColumns = 30, MaxFields = 30, MaxKeyFields = 4;

    public static readonly IReadOnlySet<string> ParserTypes = new HashSet<string>(StringComparer.Ordinal) { "table", "json", "kv", "regex" };
    public static readonly IReadOnlySet<string> ValueTypes = new HashSet<string>(StringComparer.Ordinal) { "string", "number", "bool" };
    /// <summary>Added to every row by the platform (the connection's name, and the entity within it), so a parser can't
    /// produce them.</summary>
    public static readonly IReadOnlySet<string> ReservedFields = new HashSet<string>(StringComparer.Ordinal) { "target", "entity" };

    public static readonly IReadOnlySet<string> DatasetKinds = new HashSet<string>(StringComparer.Ordinal) { "timeseries", "snapshot", "event" };

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,39}$")]
    private static partial Regex FieldNamePattern();

    /// <summary>Camel-case; unknown properties are errors so a model's typo is reported, not ignored.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static (T? Spec, string? Error) Parse<T>(string json, string what) where T : class
    {
        try
        {
            var spec = JsonSerializer.Deserialize<T>(json, Json);
            return spec is null ? (null, $"{what} is empty.") : (spec, null);
        }
        catch (JsonException ex)
        {
            return (null, $"{what} is not valid: {ex.Message}");
        }
    }

    public static string Serialize<T>(T spec)
    {
        return JsonSerializer.Serialize(spec, Json);
    }

    public static List<string> Validate(ParserSpec p)
    {
        var errors = new List<string>();
        if (p.Type is null || !ParserTypes.Contains(p.Type))
            return [$"parser.type must be one of {string.Join(", ", ParserTypes.Order())}."];

        switch (p.Type)
        {
            case "table":
                if (p.Columns is not { Count: > 0 }) errors.Add("parser.columns is required for a table parser (one entry per column, in order).");
                else ValidateColumns(p.Columns, errors, requireNames: true);
                if (p.SkipLines is < 0 or > 50) errors.Add("parser.skipLines must be between 0 and 50.");
                break;
            case "json":
                foreach (var (name, field) in p.Fields ?? [])
                {
                    if (!FieldNamePattern().IsMatch(name)) errors.Add($"parser.fields: '{name}' is not a valid field name (letters, digits, '_').");
                    else if (ReservedFields.Contains(name)) errors.Add($"parser.fields: '{name}' is reserved (the platform adds it); pick another name.");
                    if (string.IsNullOrWhiteSpace(field.Path)) errors.Add($"parser.fields.{name}.path is required.");
                    if (field.Type is not null && !ValueTypes.Contains(field.Type)) errors.Add($"parser.fields.{name}.type must be string, number or bool.");
                }
                if ((p.Fields?.Count ?? 0) > MaxFields) errors.Add($"parser.fields may have at most {MaxFields} entries.");
                break;
            case "kv":
                if (p.Columns is not null) ValidateColumns(p.Columns, errors, requireNames: true);
                break;
            case "regex":
                if (string.IsNullOrWhiteSpace(p.Pattern)) { errors.Add("parser.pattern is required for a regex parser."); break; }
                try
                {
                    var rx = new Regex(p.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
                    var groups = rx.GetGroupNames().Where(g => !int.TryParse(g, out _)).ToList();
                    if (groups.Count == 0) errors.Add("parser.pattern needs at least one named group, like (?<mount>\\S+).");
                    foreach (var g in groups.Where(ReservedFields.Contains)) errors.Add($"parser.pattern: group '{g}' is reserved (the platform adds it); pick another name.");
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"parser.pattern is not a valid regular expression: {ex.Message}");
                }
                if (p.Columns is not null) ValidateColumns(p.Columns, errors, requireNames: true);
                break;
        }
        return errors;
    }

    private static void ValidateColumns(List<ColumnSpec> columns, List<string> errors, bool requireNames)
    {
        if (columns.Count > MaxColumns) errors.Add($"parser.columns may have at most {MaxColumns} entries.");
        var seen = new HashSet<string>();
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            if (string.IsNullOrEmpty(c.Name)) { if (requireNames) errors.Add($"parser.columns[{i}].name is required."); continue; }
            if (!FieldNamePattern().IsMatch(c.Name)) errors.Add($"parser.columns[{i}].name '{c.Name}' is not a valid field name (letters, digits, '_').");
            else if (ReservedFields.Contains(c.Name)) errors.Add($"parser.columns[{i}].name '{c.Name}' is reserved (the platform adds it); pick another name.");
            else if (!seen.Add(c.Name)) errors.Add($"parser.columns: duplicate name '{c.Name}'.");
            if (c.Type is not null && !ValueTypes.Contains(c.Type)) errors.Add($"parser.columns[{i}].type must be string, number or bool.");
        }
    }

    /// <summary>Field names a parser is known to produce, or null when they can't be known (kv without columns).</summary>
    public static IReadOnlySet<string>? ProducedFields(ParserSpec p)
    {
        return p.Type switch
        {
            "table" => (p.Columns ?? []).Where(c => c.Name is not null).Select(c => c.Name!).ToHashSet(),
            "json" when p.Fields is { Count: > 0 } => p.Fields.Keys.ToHashSet(),
            "regex" when !string.IsNullOrWhiteSpace(p.Pattern) => new Regex(p.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200))
                .GetGroupNames().Where(g => !int.TryParse(g, out _)).ToHashSet(),
            _ => null
        };
    }

    public static List<string> Validate(DatasetSpec d)
    {
        var errors = new List<string>();
        if (d.Name is null || !NamePattern().IsMatch(d.Name))
            errors.Add("dataset.name must be lowercase letters, digits, '_' or '-', starting with a letter (at most 40 characters).");
        if (d.Kind is not null && !DatasetKinds.Contains(d.Kind)) errors.Add($"dataset.kind must be one of {string.Join(", ", DatasetKinds.Order())}.");
        if (d.KeyFields is { Count: > MaxKeyFields }) errors.Add($"dataset.keyFields may have at most {MaxKeyFields} entries.");
        foreach (var k in d.KeyFields ?? [])
            if (!FieldNamePattern().IsMatch(k)) errors.Add($"dataset.keyFields: '{k}' is not a valid field name.");
        return errors;
    }
}
