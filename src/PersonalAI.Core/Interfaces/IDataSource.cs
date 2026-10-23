using PersonalAI.Core.Services.Ssh;

namespace PersonalAI.Core.Interfaces;

/// <summary>Something an agent can be allowed to use as a data source: a group of servers, or a single database.</summary>
/// <param name="Kind">"group" (its members, current and future, are all in scope) or "connection" (just this one).</param>
/// <param name="ConnectionNames">The connections inside, for display in the scope picker.</param>
public record ScopeItem(Guid Id, string Name, string Kind, IReadOnlyList<string> ConnectionNames);

/// <summary>A concrete thing a request can run on (one server, one database). <c>Name</c> becomes the <c>target</c>
/// field of every row collected from it.</summary>
public record DataConnection(Guid Id, string Name);

/// <summary>The outcome of one request. <c>Connected</c> is false when the connection or authentication failed
/// (<c>Error</c> is then set); a request that ran but failed has Connected = true and a non-zero ExitCode.</summary>
public record DataSourceResult(bool Connected, int ExitCode, string Stdout, string Stderr, string? Error);

/// <summary>A tool that reads data from connections it owns (SSH servers today; databases or HTTP APIs later).
/// The collection runner and the model's collector tools only ever talk to this contract, so adding a data source
/// needs no change to them. An agent is allowed to use a source only through its scope: the scope items the user
/// selected in the agent's settings, resolved to connections here.</summary>
public interface IDataSource
{
    /// <summary>The tool's name, also what a collection job's <c>tool</c> field refers to.</summary>
    string ToolName { get; }

    string DisplayName { get; }

    /// <summary>What a request is called, for messages ("command", "query").</summary>
    string RequestNoun { get; }

    /// <summary>What this source's connections are called, for the UI ("server", "database").</summary>
    string ConnectionNoun { get; }

    /// <summary>What an agent can be scoped to, for the picker in the agent's settings.</summary>
    Task<IReadOnlyList<ScopeItem>> ListScopeItemsAsync(CancellationToken ct);

    /// <summary>The enabled connections inside the given scope items.</summary>
    Task<IReadOnlyList<DataConnection>> ResolveAsync(IReadOnlyCollection<Guid> scopeItemIds, CancellationToken ct);

    /// <summary>Whether a request may be sent at all (a tool's own rules: an allowlist, read-only statements...).</summary>
    GuardResult Check(string request);

    /// <summary>Runs the request on one connection and records the connection's status (reachable, host key).</summary>
    Task<DataSourceResult> RunAsync(Guid connectionId, string request, TimeSpan timeout, CancellationToken ct);
}
