using System.Text.Json;

namespace PersonalAI.Core.Services.Skills;

/// <summary>Shared helper for exposing Skills as AI tool definitions named "skill_&lt;slug&gt;" — used by
/// both AgentExecutionService (an agent's own skill allowlist) and ChatAgentService (a chat session's),
/// so a model can invoke a Skill the same way it invokes any other tool.</summary>
public static class SkillToolCatalog
{
    public const string ParametersSchema =
        """{"type":"object","properties":{"input":{"type":"string","description":"Instructions or input to pass to this skill's run."}},"required":[]}""";

    public static string BuildUniqueToolName(string skillName, IEnumerable<string> existingNames)
    {
        var slug = new string(skillName.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        slug = slug.Trim('_');
        if (string.IsNullOrEmpty(slug)) slug = "skill";

        var candidate = $"skill_{slug}";
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var suffix = 2;
        while (existing.Contains(candidate))
            candidate = $"skill_{slug}_{suffix++}";
        return candidate;
    }

    public static string? ExtractInputArg(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("input", out var v) &&
                   v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
