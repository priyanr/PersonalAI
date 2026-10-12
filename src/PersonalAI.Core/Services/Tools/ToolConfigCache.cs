using System.Collections.Concurrent;

namespace PersonalAI.Core.Services.Tools;

/// <summary>In-memory mirror of ToolConfig, kept in sync with the DB by the startup load and by
/// ToolsController/GmailOAuthController/GmailTool after every update, so tools can read their
/// configuration (including hidden fields like OAuth tokens) synchronously on the hot path without a
/// DB round-trip per call. Mirrors AIProviderConfigCache.</summary>
public sealed class ToolConfigCache
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string?>> _tools =
        new(StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string toolName, string fieldName) =>
        _tools.TryGetValue(toolName, out var fields) && fields.TryGetValue(fieldName, out var value)
            ? value
            : null;

    public void SetTool(string toolName, IReadOnlyDictionary<string, string?> values)
    {
        var fields = new ConcurrentDictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        _tools[toolName] = fields;
    }

    /// <summary>Merges the given field values into whatever is already cached for the tool, rather than
    /// replacing the whole set — used after a partial update (e.g. only AccessToken/TokenExpiresAtUtc
    /// changed on a token refresh) so unrelated fields aren't dropped from the cache.</summary>
    public void MergeTool(string toolName, IReadOnlyDictionary<string, string?> values)
    {
        var fields = _tools.GetOrAdd(toolName, _ => new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        foreach (var (name, value) in values)
            fields[name] = value;
    }
}
