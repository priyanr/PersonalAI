using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Monitoring.Alerts;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

public enum JobSelection
{
    /// <summary>Active jobs whose interval has elapsed (the background loop).</summary>
    Due,
    /// <summary>Every active job now ("Run now").</summary>
    All
}

/// <param name="Connected">False only when the connection itself could not be made; a request that failed on a connection
/// that answered still counts as connected.</param>
public record JobTargetResult(Guid JobId, string Job, Guid TargetId, string Target, bool Success, int Rows, string? Error, bool Connected = true);

/// <param name="Skipped">Another pass for the agent was still running.</param>
/// <param name="MonitoredTargetNames">Every connection the agent can reach, across its data sources.</param>
public record RunResult(bool Skipped, int JobsRun, IReadOnlyList<JobTargetResult> Results, IReadOnlyList<string> MonitoredTargetNames);

/// <param name="ValidationErrors">Problems with the proposed job itself; nothing was run.</param>
/// <param name="NeedsApproval">The request is not on the allowed list: it was not run, and the job can only be saved for the
/// user to approve as written.</param>
public record TestOutcome(
    bool Success, string? Target, string? Error, IReadOnlyList<string> ValidationErrors,
    IReadOnlyList<JsonObject> SampleRows, int RowCount, string? Diagnostic, bool NeedsApproval = false);

public record AdHocOutcome(bool Success, string Output, int ExitCode, string? Error);

/// <summary>Runs an agent's collection jobs: each active job's request on the connections of its tool that are inside
/// the agent's scope, parsed into rows and appended to its dataset, on the job's own interval and with no model
/// involved. It knows nothing about SSH or SQL: it talks to <see cref="IDataSource"/>. Singleton: every
/// (job, connection) pair runs in its own DI scope because a DbContext is not safe for concurrent use.</summary>
public sealed class CollectionRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<CollectionRunner> logger)
{
    private const int MaxParallel = 4;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AdHocTimeout = TimeSpan.FromSeconds(30);
    // The loop ticks every few seconds; allow this much early so a job doesn't slip a whole tick each cycle.
    private static readonly TimeSpan DueTolerance = TimeSpan.FromSeconds(10);
    private const int MaxOutputChars = 1_000_000;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _agentLocks = new();

    // ---- running jobs ---------------------------------------------------------------

    /// <summary>Returns null if the agent doesn't exist or isn't a Dashboard &amp; Alerts agent.</summary>
    public async Task<RunResult?> RunAsync(Guid agentId, JobSelection selection, Guid? onlyJobId, CancellationToken ct)
    {
        var agentLock = _agentLocks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        if (!await agentLock.WaitAsync(0, ct)) return new RunResult(true, 0, [], []);

        try
        {
            var startedAt = time.GetUtcNow();
            DashboardAlertsConfig config;
            List<CollectionJob> due;
            var pairs = new List<(CollectionJob Job, DataConnection Connection)>();
            var jobErrors = new Dictionary<Guid, string>();
            var monitoredNames = new List<string>();

            await using (var scope = scopes.CreateAsyncScope())
            {
                var agent = await scope.ServiceProvider.GetRequiredService<IAgentsRepository>().GetAsync(agentId, ct);
                if (agent is null || agent.Type != AgentType.DashboardAlerts) return null;
                config = DashboardAlertsConfig.Parse(agent.TypeConfigJson);

                var registry = scope.ServiceProvider.GetRequiredService<DataSourceRegistry>();
                var all = await scope.ServiceProvider.GetRequiredService<ICollectionJobsRepository>().GetAllAsync(agentId, ct);
                due = [.. all.Where(j => j.Status == JobStatus.Active && (onlyJobId is null || j.Id == onlyJobId)
                    && (selection == JobSelection.All || IsDue(j, startedAt)))];

                // Each tool's connections in the agent's scope, resolved once per pass.
                var byTool = new Dictionary<string, IReadOnlyList<DataConnection>>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in registry.All)
                {
                    var connections = await registry.ConnectionsAsync(source, config, ct);
                    byTool[source.ToolName] = connections;
                    monitoredNames.AddRange(connections.Select(c => c.Name));
                }

                foreach (var job in due)
                {
                    if (registry.Find(job.Tool) is null) { jobErrors[job.Id] = $"The '{job.Tool}' tool is not available."; continue; }
                    var connections = byTool.GetValueOrDefault(job.Tool) ?? [];
                    if (config.ScopeFor(job.Tool).Count == 0) { jobErrors[job.Id] = $"The '{job.Tool}' tool is not enabled for this agent: select what it may use under Data sources in the agent's settings."; continue; }

                    var only = TargetNames(job);
                    var applicable = connections.Where(c => only is null || only.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (applicable.Count == 0) { jobErrors[job.Id] = "No enabled connection in this agent's scope matches the job."; continue; }
                    pairs.AddRange(applicable.Select(c => (job, c)));
                }

                if (due.Count > 0) await EnsureDatasetsAsync(scope.ServiceProvider.GetRequiredService<IDatasetRepository>(), agentId, due, ct);
            }
            if (due.Count == 0) return new RunResult(false, 0, [], monitoredNames);

            using var gate = new SemaphoreSlim(MaxParallel);
            var results = await Task.WhenAll(pairs.Select(async p =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return await RunPairAsync(agentId, p.Job, p.Connection, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Whatever went wrong for this pair, the rest of the pass still completes and records its run.
                    logger.LogError(ex, "Job {Job} failed on {Target}.", p.Job.Name, p.Connection.Name);
                    return new JobTargetResult(p.Job.Id, p.Job.Name, p.Connection.Id, p.Connection.Name, false, 0, ex.Message);
                }
                finally { gate.Release(); }
            }));

            try
            {
                await RecordStatusAsync(agentId, pairs, results, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recording connection status for agent {AgentId} failed.", agentId);
            }

            // LastRunAt must be written even if something above went wrong: without it every job is due again on the
            // next tick and the same requests would hit every connection every few seconds.
            try
            {
                await RecordJobRunsAsync(due, results, jobErrors, startedAt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recording job runs for agent {AgentId} failed.", agentId);
            }
            await EvaluateAlertsAsync(agentId, config, ct);
            return new RunResult(false, due.Count, results, monitoredNames);
        }
        finally
        {
            agentLock.Release();
        }
    }

    private static bool IsDue(CollectionJob job, DateTimeOffset now) =>
        job.LastRunAt is null || now - job.LastRunAt.Value >= TimeSpan.FromMinutes(job.IntervalMinutes) - DueTolerance;

    private static List<string>? TargetNames(CollectionJob job) =>
        string.IsNullOrEmpty(job.TargetsJson) ? null : JsonSerializer.Deserialize<List<string>>(job.TargetsJson);

    private static async Task EnsureDatasetsAsync(IDatasetRepository datasets, Guid agentId, IEnumerable<CollectionJob> jobs, CancellationToken ct)
    {
        // Created up front, sequentially: parallel (job, connection) runs would otherwise race to create the same
        // (agent, name) dataset row.
        var definitions = new Dictionary<string, DatasetDefinition> { [DataSourceStatus.DatasetName] = DataSourceStatus.Definition };
        foreach (var job in jobs)
        {
            var (dataset, _) = CollectorSpecs.Parse<DatasetSpec>(job.DatasetJson, "dataset");
            if (dataset?.Name is { } name) definitions[name] = new DatasetDefinition(name, KindOf(dataset.Kind), job.Description);
        }
        await datasets.EnsureDatasetsAsync(agentId, definitions.Values.ToList(), ct);
    }

    private async Task<JobTargetResult> RunPairAsync(Guid agentId, CollectionJob job, DataConnection connection, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var datasets = scope.ServiceProvider.GetRequiredService<IDatasetRepository>();
        var source = scope.ServiceProvider.GetRequiredService<DataSourceRegistry>().Find(job.Tool)!;

        var ts = time.GetUtcNow().UtcDateTime;
        var points = new List<NewDataPoint>();
        string? error = null;
        var rows = 0;
        var couldNotConnect = false;

        try
        {
            var (parser, parserError) = CollectorSpecs.Parse<ParserSpec>(job.ParserJson, "parser");
            if (parser is null) throw new InvalidOperationException(parserError);

            // Not on the allowed list is acceptable only for a job the person approved as such; anything the tool refuses
            // outright (chaining, redirects...) never runs, whatever was approved.
            var guard = source.Check(job.Request);
            if (!guard.Allowed && !(job.OutsideAllowlist && guard.CanBeApproved))
                throw new InvalidOperationException($"The {source.RequestNoun} is no longer allowed: {guard.Error}");

            var result = await source.RunAsync(connection.Id, job.Request, JobTimeout, ct);
            couldNotConnect = !result.Connected;
            error = Failure(result);
            if (error is null)
            {
                var stdout = Cap(result.Stdout);
                var (dataset, datasetError) = CollectorSpecs.Parse<DatasetSpec>(job.DatasetJson, "dataset");
                if (dataset is null) throw new InvalidOperationException(datasetError);
                var parsed = CollectorParser.Parse(parser, stdout);
                points = BuildPoints(dataset, connection, parsed.Rows, ts);
                rows = points.Count;
                // Output that yields no rows is a mismatched parser, worth showing on the job. But a request that
                // returns nothing is a legitimate result (the failing-units list when nothing is failing).
                if (rows == 0 && !string.IsNullOrWhiteSpace(stdout)) error = parsed.Diagnostic ?? "No rows were produced.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Job {Job} failed on {Target}.", job.Name, connection.Name);
            error = ex.Message;
        }

        try
        {
            await datasets.AddPointsAsync(agentId, points, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Storing results of job {Job} for {Target} failed.", job.Name, connection.Name);
            error ??= $"The results could not be stored: {ex.Message}";
            rows = 0;
        }
        return new JobTargetResult(job.Id, job.Name, connection.Id, connection.Name, error is null, rows, error, Connected: !couldNotConnect);
    }

    /// <summary>One point per connection for the pass: could it be reached? Written whatever the tool, so "server down" or
    /// "database unavailable" is data an alert rule can fire on.</summary>
    private async Task RecordStatusAsync(
        Guid agentId, List<(CollectionJob Job, DataConnection Connection)> pairs, JobTargetResult[] results, CancellationToken ct)
    {
        var ts = time.GetUtcNow().UtcDateTime;
        var points = new List<NewDataPoint>();
        foreach (var group in pairs.Zip(results).GroupBy(x => (x.First.Job.Tool, x.First.Connection.Id)))
        {
            var failed = group.FirstOrDefault(x => !x.Second.Connected);
            points.Add(DataSourceStatus.Point(group.First().First.Connection, group.Key.Tool,
                available: failed.Second is null, failed.Second?.Error, ts));
        }
        if (points.Count == 0) return;

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agentId, points, ct);
    }

    private async Task RecordJobRunsAsync(
        List<CollectionJob> jobs, IReadOnlyList<JobTargetResult> results, Dictionary<Guid, string> jobErrors, DateTimeOffset startedAt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICollectionJobsRepository>();
        foreach (var job in jobs)
        {
            var mine = results.Where(r => r.JobId == job.Id).ToList();
            var firstError = mine.FirstOrDefault(r => r.Error is not null);
            var error = jobErrors.GetValueOrDefault(job.Id) ?? (firstError is null ? null : $"{firstError.Target}: {firstError.Error}");
            // LastRunAt is the start of the pass, so the next one stays on cadence however long this one took.
            await repo.RecordRunAsync(job.Id, startedAt, mine.Sum(r => r.Rows), error, ct);
        }
    }

    // ---- dry run and ad-hoc requests ------------------------------------------------

    /// <summary>Runs a proposed job once on one connection and reports what it would produce, without saving anything:
    /// how a model checks its request and parser before <c>collector_save</c>.</summary>
    public async Task<TestOutcome> TestAsync(Guid agentId, JobInput input, string? connectionName, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var validation = await scope.ServiceProvider.GetRequiredService<CollectionJobService>().ValidateDetailedAsync(agentId, input, existing: null, ct);
        // Name and job-count problems don't matter for a dry run.
        var errors = validation.Errors.Where(e => !e.StartsWith("a job named", StringComparison.Ordinal) && !e.StartsWith("at most", StringComparison.Ordinal)).ToList();
        if (errors.Count > 0) return new TestOutcome(false, null, null, errors, [], 0, null);
        if (validation.OutsideAllowlist) return new TestOutcome(false, null, null, [], [], 0, null, NeedsApproval: true);

        var tool = (await scope.ServiceProvider.GetRequiredService<CollectionJobService>().ResolveToolAsync(agentId, input.Tool, ct)).Tool!;
        var resolved = await ResolveConnectionAsync(scope.ServiceProvider, agentId, tool, connectionName, ct);
        if (resolved.Source is null || resolved.Connection is null) return new TestOutcome(false, null, resolved.Error, [], [], 0, null);

        var result = await resolved.Source.RunAsync(resolved.Connection.Id, input.Request!.Trim(), JobTimeout, ct);
        var failure = Failure(result);
        if (failure is not null) return new TestOutcome(false, resolved.Connection.Name, failure, [], [], 0, null);

        var stdout = Cap(result.Stdout);
        var parsed = CollectorParser.Parse(input.Parser!, stdout);
        if (parsed.Rows.Count == 0 && string.IsNullOrWhiteSpace(stdout))
            return new TestOutcome(true, resolved.Connection.Name, null, [], [], 0,
                "The request returned nothing. That is fine for a check that only returns rows when something is wrong; make sure it is what you expect.");
        return new TestOutcome(parsed.Rows.Count > 0, resolved.Connection.Name, parsed.Rows.Count == 0 ? parsed.Diagnostic : null, [],
            parsed.Rows.Take(5).ToList(), parsed.Rows.Count, parsed.Diagnostic);
    }

    /// <summary>One request on one of the agent's connections of the given tool, for a model exploring before it
    /// designs a job. The tool's own guard decides whether it may run.</summary>
    public async Task<AdHocOutcome> RunCommandAsync(Guid agentId, string tool, string? connectionName, string request, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var resolved = await ResolveConnectionAsync(scope.ServiceProvider, agentId, tool, connectionName, ct);
        if (resolved.Source is null || resolved.Connection is null) return new AdHocOutcome(false, "", -1, resolved.Error);

        var guard = resolved.Source.Check(request);
        if (!guard.Allowed)
            return new AdHocOutcome(false, "", -1, guard.CanBeApproved
                ? guard.Error + " It was not run. If you need it, put it in a collection job with collector_save: the user is asked to approve that exact request, and you can read its results once it runs."
                : guard.Error);

        var result = await resolved.Source.RunAsync(resolved.Connection.Id, request, AdHocTimeout, ct);
        if (!result.Connected || result.Error is not null) return new AdHocOutcome(false, "", -1, result.Error ?? "Could not connect.");

        var output = Cap(result.Stdout) + (string.IsNullOrWhiteSpace(result.Stderr) ? "" : $"\n[stderr] {result.Stderr.Trim()}");
        return new AdHocOutcome(result.ExitCode == 0, output, result.ExitCode, result.ExitCode == 0 ? null : $"Exit code {result.ExitCode}");
    }

    private sealed record Resolved(IDataSource? Source, DataConnection? Connection, string? Error);

    /// <summary>The named connection among those of <paramref name="tool"/> in the agent's scope, or the first when no
    /// name is given. Anything outside the scope is unreachable here.</summary>
    private static async Task<Resolved> ResolveConnectionAsync(IServiceProvider sp, Guid agentId, string tool, string? name, CancellationToken ct)
    {
        var agent = await sp.GetRequiredService<IAgentsRepository>().GetAsync(agentId, ct);
        if (agent is null || agent.Type != AgentType.DashboardAlerts)
            return new Resolved(null, null, "This tool is only available to Dashboard & Alerts agents.");

        var registry = sp.GetRequiredService<DataSourceRegistry>();
        var source = registry.Find(tool);
        if (source is null) return new Resolved(null, null, $"There is no '{tool}' tool.");

        var config = DashboardAlertsConfig.Parse(agent.TypeConfigJson);
        if (config.ScopeFor(source.ToolName).Count == 0)
            return new Resolved(null, null, $"The {source.DisplayName} tool is not enabled for this agent: select what it may use under Data sources in the agent's settings.");

        var connections = await registry.ConnectionsAsync(source, config, ct);
        if (connections.Count == 0) return new Resolved(null, null, $"This agent has no enabled {source.DisplayName} connections in its scope.");

        if (string.IsNullOrWhiteSpace(name)) return new Resolved(source, connections[0], null);
        var match = connections.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return match is null
            ? new Resolved(null, null, $"No connection named '{name}'. This agent can use: {string.Join(", ", connections.Select(c => c.Name))}.")
            : new Resolved(source, match, null);
    }

    // ---- maintenance ----------------------------------------------------------------

    /// <summary>Deletes raw points and resolved alerts older than the agent's retention. Returns the rows removed.</summary>
    public async Task<int> PruneAsync(Guid agentId, DashboardAlertsConfig config, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-config.RetentionDays);
        var points = await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().PruneAsync(agentId, cutoff, ct);
        var alerts = await scope.ServiceProvider.GetRequiredService<IAlertsRepository>().PruneResolvedAsync(agentId, cutoff, ct);
        return points + alerts;
    }

    /// <summary>Alert evaluation follows every pass that ran jobs. It must never fail the collection itself.</summary>
    private async Task EvaluateAlertsAsync(Guid agentId, DashboardAlertsConfig config, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var staleAfter = TimeSpan.FromMinutes(Math.Max(15, 3 * config.CollectIntervalMinutes));
            var freshness = CollectionJobService.FreshnessWindows(
                await scope.ServiceProvider.GetRequiredService<ICollectionJobsRepository>().GetAllAsync(agentId, ct));
            var result = await scope.ServiceProvider.GetRequiredService<AlertEvaluator>()
                .EvaluateAsync(agentId, time.GetUtcNow().UtcDateTime, staleAfter, ct, freshness);
            if (result.Fired > 0 || result.Resolved > 0)
                logger.LogInformation("Alerts for agent {AgentId}: {Fired} fired, {Resolved} resolved.", agentId, result.Fired, result.Resolved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Alert evaluation for agent {AgentId} failed.", agentId);
        }
    }

    // ---- helpers --------------------------------------------------------------------

    /// <summary>Rows to data points: every row gets the connection's name as "target"; an entity is the connection, plus
    /// the dataset's key fields when it has them (a row missing a key field is skipped).</summary>
    public static List<NewDataPoint> BuildPoints(DatasetSpec dataset, DataConnection connection, IEnumerable<JsonObject> rows, DateTime ts)
    {
        var kind = KindOf(dataset.Kind);
        var keys = dataset.KeyFields ?? [];
        var points = new List<NewDataPoint>();
        foreach (var source in rows)
        {
            var values = (JsonObject)source.DeepClone();
            var keyParts = new List<string>();
            foreach (var key in keys)
                if (values[key]?.ToString() is { Length: > 0 } v) keyParts.Add(v);
            if (keyParts.Count != keys.Count) continue;

            values["target"] = connection.Name;
            if (keyParts.Count > 0) values["entity"] = string.Join('/', keyParts);
            var entityKey = keyParts.Count > 0 ? $"{connection.Id}|{string.Join('/', keyParts)}" : connection.Id.ToString();
            points.Add(new NewDataPoint(dataset.Name!, kind, entityKey, ts, values.ToJsonString()));
        }
        return points;
    }

    private static DatasetKind KindOf(string? kind) => kind switch
    {
        "snapshot" => DatasetKind.Snapshot,
        "event" => DatasetKind.Event,
        _ => DatasetKind.Timeseries
    };

    private static string? Failure(DataSourceResult r)
    {
        if (!r.Connected || r.Error is not null) return r.Error ?? "Could not connect.";
        return r.ExitCode == 0 ? null : $"Request failed with code {r.ExitCode}: {r.Stderr.Trim()}";
    }

    private static string Cap(string text) => text.Length <= MaxOutputChars ? text : text[..MaxOutputChars];
}
