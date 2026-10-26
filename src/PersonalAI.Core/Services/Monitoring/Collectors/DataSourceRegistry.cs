using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Services.Agents.Types;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

/// <summary>The data sources registered with the app, and the rule for what an agent may reach through them: only the
/// scope items in its own <c>toolScopes</c>. Scoped, like the sources (they use a DbContext).</summary>
public sealed class DataSourceRegistry(IEnumerable<IDataSource> sources)
{
    private readonly Dictionary<string, IDataSource> _byTool = sources.ToDictionary(s => s.ToolName, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IDataSource> All => _byTool.Values;

    public IDataSource? Find(string? tool)
    {
        return tool is not null && _byTool.TryGetValue(tool, out var s) ? s : null;
    }

    /// <summary>Data sources this agent is scoped to: the registered ones with at least one scope item selected.</summary>
    public IReadOnlyList<IDataSource> ForAgent(DashboardAlertsConfig config)
    {
        return [.. _byTool.Values.Where(s => config.ScopeFor(s.ToolName).Count > 0).OrderBy(s => s.ToolName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The connections of one tool the agent may reach. Empty when the tool isn't in its scope.</summary>
    public async Task<IReadOnlyList<DataConnection>> ConnectionsAsync(IDataSource source, DashboardAlertsConfig config, CancellationToken ct)
    {
        var scope = config.ScopeFor(source.ToolName);
        return scope.Count == 0 ? [] : await source.ResolveAsync(scope, ct);
    }
}
