using System.Text.Json;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Tools;

namespace PersonalAI.Core.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>JSON Schema (object) describing the tool's arguments — fed into Ollama's tools[].function.parameters.</summary>
    string ParametersSchemaJson { get; }

    /// <summary>Declarative settings fields this tool needs (e.g. OAuth client id/secret), rendered by
    /// the generic /api/tools UI. Empty for tools that need no configuration — the default for all
    /// existing tools, which need no changes to pick this up.</summary>
    IReadOnlyList<ToolSettingField> SettingsFields => [];

    /// <summary>Whether this tool is "connected" beyond just having its required fields filled in (e.g.
    /// an OAuth token was actually obtained). Tools with no such concept default to false-less relevance
    /// since /api/tools only surfaces IsConnected for tools that opted into SettingsFields.</summary>
    bool IsConnected(ToolConfigCache cache) => false;

    /// <summary>True for tools that only make sense inside an agent run (they need the calling agent's servers,
    /// datasets or dashboard). They are left out of chat and the skill tool picker, so their schemas don't cost
    /// prompt tokens on every chat turn; an agent gets them through its own tool allowlist or its type's required tools.</summary>
    bool RequiresAgentRun => false;

    Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct);
}

internal static class JsonArgumentExtensions
{
    public static string? GetStringArg(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int? GetIntArg(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    public static IReadOnlyList<string>? GetStringArrayArg(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)];
    }

    public static IReadOnlyList<int>? GetIntArrayArg(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32())];
    }
}
