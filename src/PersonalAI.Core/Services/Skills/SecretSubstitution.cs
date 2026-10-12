using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PersonalAI.Core.Services.Skills;

/// <summary>Substitutes "{{secret.KEY}}" placeholders inside tool call argument strings with the real
/// secret value, for keys the skill declared and that resolved to a value — applied to the arguments
/// JSON immediately before a tool executes, so the model only ever reads/writes the placeholder token
/// and never the raw secret.</summary>
public static partial class SecretSubstitution
{
    [GeneratedRegex(@"\{\{secret\.([A-Za-z0-9_\-]+)\}\}")]
    private static partial Regex PlaceholderPattern();

    public static JsonElement Apply(JsonElement root, IReadOnlyDictionary<string, string> secrets)
    {
        if (secrets.Count == 0) return root;

        var node = JsonNode.Parse(root.GetRawText());
        if (node is null) return root;

        Substitute(node, secrets);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Substitute(JsonNode node, IReadOnlyDictionary<string, string> secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    obj[key] = SubstituteValue(obj[key], secrets);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    arr[i] = SubstituteValue(arr[i], secrets);
                break;
        }
    }

    private static JsonNode? SubstituteValue(JsonNode? value, IReadOnlyDictionary<string, string> secrets)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var s))
            return ReplacePlaceholders(s, secrets);

        if (value is not null)
            Substitute(value, secrets);
        return value;
    }

    private static string ReplacePlaceholders(string input, IReadOnlyDictionary<string, string> secrets) =>
        PlaceholderPattern().Replace(input, m => secrets.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}
