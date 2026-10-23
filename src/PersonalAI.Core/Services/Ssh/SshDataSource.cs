using PersonalAI.Core.Interfaces;

namespace PersonalAI.Core.Services.Ssh;

/// <summary>SSH as a data source: its connections are the servers in server groups, a scope item is a group (current
/// and future members), a request is one command checked by the allowlist guard, and running it also records the
/// server's status (last seen or error, and the pinned host key on first success).</summary>
public sealed class SshDataSource(ISshServersRepository servers, ISshRunner ssh, SshGuardProvider guards) : IDataSource
{
    public string ToolName => SshGuardProvider.ToolName;
    public string DisplayName => "SSH";
    public string RequestNoun => "command";
    public string ConnectionNoun => "server";

    public async Task<IReadOnlyList<ScopeItem>> ListScopeItemsAsync(CancellationToken ct)
    {
        var all = await servers.GetServersAsync(null, ct);
        var items = new List<ScopeItem>();
        foreach (var group in await servers.GetGroupsAsync(ct))
        {
            var names = all.Where(t => t.GroupId == group.Id).Select(t => t.Name).Order().ToList();
            items.Add(new ScopeItem(group.Id, group.Name, "group", names));
        }
        return items;
    }

    public async Task<IReadOnlyList<DataConnection>> ResolveAsync(IReadOnlyCollection<Guid> scopeItemIds, CancellationToken ct)
    {
        var ids = scopeItemIds.ToHashSet();
        return [.. (await servers.GetServersAsync(null, ct))
            .Where(t => t.Enabled && ids.Contains(t.GroupId))
            .Select(t => new DataConnection(t.Id, t.Name))];
    }

    public GuardResult Check(string request) => guards.Current.Check(request);

    public async Task<DataSourceResult> RunAsync(Guid connectionId, string request, TimeSpan timeout, CancellationToken ct)
    {
        var server = await servers.GetServerAsync(connectionId, ct);
        if (server is null) return new DataSourceResult(false, -1, "", "", "Server no longer exists.");

        var result = await ssh.RunAsync(server, request, timeout, ct);

        // The server's own status is about reaching it (connecting and authenticating), not about whether this particular
        // command succeeded or finished in time: a timeout after connecting still means the server is up.
        await servers.RecordConnectionAsync(connectionId, result.Connected, result.Connected ? null : result.Error ?? "Could not connect.",
            result.Connected ? result.HostKeyFingerprint : null, ct);
        return new DataSourceResult(result.Connected, result.ExitCode, result.Stdout, result.Stderr, result.Error);
    }
}
