using System.Text.Json;

namespace PersonalAI.Core.Services.Agents.Types;

/// <summary>Per-agent settings for AgentType.DashboardAlerts, stored as Agent.TypeConfigJson. The agent's
/// own Prompt describes what to build; the LLM schedule is the agent's normal ScheduleKind.</summary>
public record DashboardAlertsConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Parses stored TypeConfigJson (already normalized on save); falls back to defaults if unreadable.
    /// A config saved before data sources were generic has <c>targetGroupIds</c>; it is read as the SSH scope.</summary>
    public static DashboardAlertsConfig Parse(string? json)
    {
        try
        {
            return ParseStrict(json);
        }
        catch (JsonException)
        {
            return new DashboardAlertsConfig();
        }
    }

    /// <summary>Like <see cref="Parse"/> but throws <see cref="JsonException"/> on invalid JSON, so a save can reject it.
    /// Reads the legacy <c>targetGroupIds</c> too, which is how an agent created before data sources were generic keeps
    /// its servers when its settings are saved again.</summary>
    public static DashboardAlertsConfig ParseStrict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DashboardAlertsConfig();

        var config = JsonSerializer.Deserialize<DashboardAlertsConfig>(json, JsonOptions) ?? new DashboardAlertsConfig();
        // "toolScopes": null, or a tool whose list is null, is treated as nothing selected rather than a crash.
        var scopes = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tool, ids) in config.ToolScopes ?? [])
            if (ids is not null) scopes[tool] = [.. ids.Distinct()];

        using var doc = JsonDocument.Parse(json);
        if (!scopes.ContainsKey("ssh") && doc.RootElement.TryGetProperty("targetGroupIds", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
        {
            var ids = legacy.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
                .Select(e => Guid.Parse(e.GetString()!)).ToList();
            if (ids.Count > 0) scopes["ssh"] = ids;
        }
        return config with { ToolScopes = scopes };
    }

    public const int MinCollectIntervalMinutes = 1;
    public const int MaxCollectIntervalMinutes = 5;

    /// <summary>For each data tool (by tool name, e.g. "ssh"), the scope items this agent may use: groups of
    /// connections, or single connections, as the tool offers them. A tool with no entry (or an empty one) isn't
    /// available to the agent. This is the boundary for everything the agent can reach through that tool.</summary>
    public Dictionary<string, List<Guid>> ToolScopes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scope items selected for a tool; empty when the tool is not enabled for this agent.</summary>
    public IReadOnlyCollection<Guid> ScopeFor(string tool) =>
        ToolScopes.FirstOrDefault(p => string.Equals(p.Key, tool, StringComparison.OrdinalIgnoreCase)).Value ?? [];

    /// <summary>Default interval, in minutes, for model-created jobs that don't set their own. Jobs run on their own intervals with no model involved.</summary>
    public int CollectIntervalMinutes { get; init; } = 5;

    /// <summary>Jobs the model creates or changes wait for the user to approve the exact command before they run
    /// unattended. On by default; turning it off lets the model's jobs run as soon as they pass validation.</summary>
    public bool RequireJobApproval { get; init; } = true;

    /// <summary>Raw data points older than this are pruned (rollups are kept longer).</summary>
    public int RetentionDays { get; init; } = 30;
}
