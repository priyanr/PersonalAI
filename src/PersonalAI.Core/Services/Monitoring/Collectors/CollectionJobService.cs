using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Ssh;
using PersonalAI.Core.Services.Agents;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

/// <summary>What a model (or the UI) proposes: a request for a data tool (a command, a query...), how to parse the answer,
/// where to put the rows, and how often. <c>Tool</c> may be left out when the agent has exactly one data tool.</summary>
public record JobInput(
    string? Name, string? Description, string? Request, int? IntervalMinutes,
    ParserSpec? Parser, DatasetSpec? Dataset, List<string>? Targets, string? Tool = null);

public record JobSaveResult(CollectionJob? Job, bool Created, IReadOnlyList<string> Errors)
{
    public bool IsValid => Job is not null && Errors.Count == 0;
}

/// <summary>What validating a proposed job found. <c>OutsideAllowlist</c>: the request is refused only because it is not on
/// the tool's allowed list, so the job can exist but only after the person approves that exact text.</summary>
public record JobValidation(List<string> Errors, bool OutsideAllowlist);

/// <summary>Creates, changes, approves and removes an agent's collection jobs. The rules that matter for safety
/// live here: a request must pass the tool's guard (or, when it merely isn't on the allowed list, be approved by the user
/// as written), a job the model creates (or changes in a way that alters what
/// runs) waits for the user's approval when the agent requires it, and a model can only touch jobs it wrote.</summary>
public sealed class CollectionJobService(
    AppDbContext db, ICollectionJobsRepository jobs, IAgentsRepository agents, DataSourceRegistry sources, CollectionRunner runner)
{
    public const int MaxJobs = 20, MaxName = 100, MinInterval = 1, MaxInterval = 1440, MaxTargets = 20;

    // ---- validation ----------------------------------------------------------------

    public async Task<List<string>> ValidateAsync(Guid agentId, JobInput i, CollectionJob? existing, CancellationToken ct)
    {
        return (await ValidateDetailedAsync(agentId, i, existing, ct)).Errors;
    }

    public async Task<JobValidation> ValidateDetailedAsync(Guid agentId, JobInput i, CollectionJob? existing, CancellationToken ct)
    {
        var errors = new List<string>();
        var outsideAllowlist = false;

        var name = i.Name?.Trim();
        if (string.IsNullOrEmpty(name)) errors.Add("name is required.");
        else if (name.Length > MaxName) errors.Add($"name must be at most {MaxName} characters.");
        else
        {
            var clash = await jobs.GetByNameAsync(agentId, name, ct);
            if (clash is not null && clash.Id != existing?.Id) errors.Add($"a job named '{clash.Name}' already exists.");
        }
        if (existing is null && (await jobs.GetAllAsync(agentId, ct)).Count >= MaxJobs)
            errors.Add($"at most {MaxJobs} jobs are allowed; delete one first.");
        if (i.Description is { Length: > 500 }) errors.Add("description must be at most 500 characters.");

        // The tool must exist and be enabled for this agent (something selected under its Data sources), and the
        // request must pass that tool's own guard.
        var (tool, toolError) = await ResolveToolAsync(agentId, i.Tool, ct);
        var source = tool is null ? null : sources.Find(tool);
        if (toolError is not null)
            errors.Add(toolError);
        else if (source is null)
            errors.Add($"tool '{tool}' does not exist. Available: {string.Join(", ", sources.All.Select(s => s.ToolName).Order())}.");
        else
        {
            var agent = await agents.GetAsync(agentId, ct);
            if (agent is not null && DashboardAlertsConfig.Parse(agent.TypeConfigJson).ScopeFor(source.ToolName).Count == 0)
                errors.Add($"the {source.DisplayName} tool is not enabled for this agent: the user must select what it may use under Data sources in the agent's settings.");
        }

        if (string.IsNullOrWhiteSpace(i.Request)) errors.Add("request is required.");
        else if (source is not null)
        {
            var guard = source.Check(i.Request);
            if (guard is { Allowed: false, CanBeApproved: true }) outsideAllowlist = true; // the user may approve this exact text
            else if (!guard.Allowed) errors.Add($"{source.RequestNoun}: {guard.Error}");
        }

        if (i.IntervalMinutes is < MinInterval or > MaxInterval) errors.Add($"intervalMinutes must be between {MinInterval} and {MaxInterval}.");

        if (i.Parser is null) errors.Add("parser is required.");
        else
        {
            errors.AddRange(CollectorSpecs.Validate(i.Parser));
        }

        if (i.Dataset is null) errors.Add("dataset is required.");
        else
        {
            var datasetErrors = CollectorSpecs.Validate(i.Dataset);
            errors.AddRange(datasetErrors);
            if (datasetErrors.Count == 0 && i.Parser is not null && errors.All(e => !e.StartsWith("parser", StringComparison.Ordinal)))
            {
                var produced = CollectorSpecs.ProducedFields(i.Parser);
                if (produced is not null)
                    foreach (var key in i.Dataset.KeyFields ?? [])
                        if (!produced.Contains(key)) errors.Add($"dataset.keyFields: '{key}' is not a field the parser produces ({string.Join(", ", produced.Order())}).");
            }
        }

        if (i.Targets is { Count: > MaxTargets }) errors.Add($"targets may list at most {MaxTargets} connections.");
        return new JobValidation(errors, outsideAllowlist);
    }

    // ---- model / UI writes ---------------------------------------------------------

    /// <summary>Create-or-update by name, as the model does. Refuses to touch jobs it didn't write. A new job, or an
    /// edit that changes what runs, waits for approval when the agent requires it.</summary>
    public async Task<JobSaveResult> UpsertByNameAsync(Guid agentId, JobInput input, string source, CancellationToken ct)
    {
        var existing = string.IsNullOrWhiteSpace(input.Name) ? null : await jobs.GetByNameAsync(agentId, input.Name.Trim(), ct);
        if (existing is not null && existing.Source != source)
            return new JobSaveResult(null, false, [$"Job '{existing.Name}' was created by the user; you can't change it. Choose a different name."]);

        var validation = await ValidateDetailedAsync(agentId, input, existing, ct);
        if (validation.Errors.Count > 0) return new JobSaveResult(null, false, validation.Errors);

        // A request outside the allowed list always waits for the person, whatever the agent's approval setting says.
        var outsideAllowlist = validation.OutsideAllowlist;
        var requireApproval = outsideAllowlist || await RequireApprovalAsync(agentId, ct);
        var interval = input.IntervalMinutes ?? await DefaultIntervalAsync(agentId, ct);
        var parserJson = CollectorSpecs.Serialize(input.Parser!);
        var datasetJson = CollectorSpecs.Serialize(input.Dataset!);
        List<string> targets = [.. (input.Targets ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        var targetsJson = targets.Count > 0 ? JsonSerializer.Serialize(targets) : null;

        if (existing is null)
        {
            var created = new CollectionJob
            {
                AgentId = agentId, Name = input.Name!.Trim(), Description = Clean(input.Description),
                Tool = (await ResolveToolAsync(agentId, input.Tool, ct)).Tool!,
                Request = input.Request!.Trim(), IntervalMinutes = interval, ParserJson = parserJson, DatasetJson = datasetJson,
                TargetsJson = targetsJson, Source = source, OutsideAllowlist = outsideAllowlist,
                Status = requireApproval ? JobStatus.PendingApproval : JobStatus.Active
            };
            db.CollectionJobs.Add(created);
            await db.SaveChangesAsync(ct);
            return new JobSaveResult(created, true, []);
        }

        // Anything that changes what runs, where, or where the results go is a new thing to approve.
        var tool = (await ResolveToolAsync(agentId, input.Tool, ct)).Tool!;
        var changesWhatRuns = existing.Request != input.Request!.Trim() || existing.ParserJson != parserJson
            || existing.DatasetJson != datasetJson || existing.TargetsJson != targetsJson || existing.Tool != tool
            || existing.OutsideAllowlist != outsideAllowlist;
        existing.OutsideAllowlist = outsideAllowlist;
        existing.Tool = tool;
        existing.Name = input.Name!.Trim();
        existing.Description = Clean(input.Description);
        existing.Request = input.Request!.Trim();
        existing.IntervalMinutes = interval;
        existing.ParserJson = parserJson;
        existing.DatasetJson = datasetJson;
        existing.TargetsJson = targetsJson;
        if (changesWhatRuns)
            existing.Status = requireApproval ? JobStatus.PendingApproval : (existing.Status == JobStatus.Disabled ? JobStatus.Disabled : JobStatus.Active);
        await SaveJobAsync(existing, ct);
        return new JobSaveResult(existing, false, []);
    }

    /// <summary>Deletes by name. Returns an error string, or null on success. A model may delete only its own jobs.</summary>
    public async Task<string?> DeleteByNameAsync(Guid agentId, string name, string source, CancellationToken ct)
    {
        var job = await jobs.GetByNameAsync(agentId, name, ct);
        if (job is null) return $"No job named '{name}'.";
        if (job.Source != source) return $"Job '{job.Name}' was created by the user; you can't delete it.";
        db.CollectionJobs.Remove(job);
        await db.SaveChangesAsync(ct);
        return null;
    }

    // ---- user decisions ------------------------------------------------------------

    /// <summary>Identifies exactly what a person is shown when asked to approve a job (its tool, request, whether that
    /// request is outside the allowed list, parser, dataset and connections). Approving requires this token back, so a job the model re-saved with a different request after the
    /// person loaded the page can't be approved by a click on the stale card.</summary>
    public static string ApprovalToken(CollectionJob job)
    {
        var text = string.Join("\u001f", job.Id, job.Tool, job.Request, job.OutsideAllowlist, job.ParserJson, job.DatasetJson, job.TargetsJson ?? "");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    public async Task<List<CollectionJobDto>> GetJobsAsync(Guid agentId, CancellationToken ct)
    {
        await EnsureDashboardAgent(agentId, ct);
        return (await jobs.GetAllAsync(agentId, ct)).Select(ToDto).ToList();
    }

    /// <summary>Approves a pending job so it runs from now on, but only if it is still exactly what the approver was
    /// shown (<paramref name="approvalToken"/>, from the job's DTO).</summary>
    public async Task<CollectionJobDto> ApproveAsync(Guid agentId, Guid jobId, string? approvalToken, CancellationToken ct)
    {
        await EnsureDashboardAgent(agentId, ct);
        if (string.IsNullOrEmpty(approvalToken)) throw new InvalidRequestException("The approval token of the job you reviewed is required.");

        var job = await jobs.GetAsync(agentId, jobId, ct);
        if (job is null || job.Status != JobStatus.PendingApproval) throw new ConflictException("The job is not waiting for approval.");

        if (!string.Equals(approvalToken, ApprovalToken(job), StringComparison.Ordinal))
            throw new ConflictException("The job changed since you reviewed it. Reload, read the new request, and approve again.");

        // The request is re-checked at approval: the allowlist may have been tightened since the model proposed it. A job
        // flagged OutsideAllowlist is approved knowing it is not on the list, but it must still be only "not listed",
        // never something the tool refuses outright.
        var guard = sources.Find(job.Tool)?.Check(job.Request);
        if (guard is null || !(guard.Allowed || (job.OutsideAllowlist && guard.CanBeApproved)))
            throw new ConflictException("The job's request is no longer allowed by the tool's allowed list.");
        job.OutsideAllowlist = !guard.Allowed; // the list may have been extended since: then it is simply allowed
        job.Status = JobStatus.Active;
        await SaveJobAsync(job, ct);
        return ToDto(job);
    }

    /// <summary>Pauses an active job, or resumes a paused one. A job still waiting for approval can only be approved
    /// or deleted, so enabling can't bypass approval.</summary>
    public async Task<CollectionJobDto> SetEnabledAsync(Guid agentId, Guid jobId, bool enabled, CancellationToken ct)
    {
        await EnsureDashboardAgent(agentId, ct);
        var job = await jobs.GetAsync(agentId, jobId, ct);
        if (job is null || job.Status == JobStatus.PendingApproval)
            throw new ConflictException("The job does not exist or is still waiting for approval (approve or delete it).");

        job.Status = enabled ? JobStatus.Active : JobStatus.Disabled;
        await SaveJobAsync(job, ct);
        return ToDto(job);
    }

    public async Task DeleteAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(agentId, jobId, ct) ?? throw new NotFoundException($"Job {jobId} not found.");
        db.CollectionJobs.Remove(job);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Runs every active job of the agent now, or only <paramref name="jobId"/>, on every connection it applies to.</summary>
    public async Task<RunNowResultDto> RunNowAsync(Guid agentId, Guid? jobId, CancellationToken ct)
    {
        if (jobId is { } id)
        {
            await EnsureDashboardAgent(agentId, ct);
            var job = await jobs.GetAsync(agentId, id, ct) ?? throw new NotFoundException($"Job {id} not found.");
            if (job.Status != JobStatus.Active) throw new ConflictException("Only an active job can be run. Approve or enable it first.");
        }

        var result = await runner.RunAsync(agentId, JobSelection.All, jobId, ct)
            ?? throw new InvalidRequestException("Agent not found or not a Dashboard & Alerts agent.");
        if (result.Skipped) throw new ConflictException("A collection pass is already running for this agent.");
        return new RunNowResultDto(result.JobsRun, result.Results.Select(r => new RunNowItemDto(r.Job, r.Target, r.Success, r.Rows, r.Error)).ToList());
    }

    // ---- helpers -------------------------------------------------------------------

    /// <summary>The tool a job uses: the one it names, or, when it names none, the agent's only data tool. An agent with
    /// several must say which; one with none can't have jobs yet.</summary>
    public async Task<(string? Tool, string? Error)> ResolveToolAsync(Guid agentId, string? named, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(named)) return (named.Trim().ToLowerInvariant(), null);

        var agent = await agents.GetAsync(agentId, ct);
        var config = agent is null ? null : DashboardAlertsConfig.Parse(agent.TypeConfigJson);
        var enabled = (config?.ToolScopes?.AsEnumerable() ?? [])
            .Where(p => p.Value.Count > 0 && sources.Find(p.Key) is not null).Select(p => p.Key.ToLowerInvariant()).Order().ToList();

        return enabled.Count switch
        {
            1 => (enabled[0], null),
            0 => (null, "no data tool is enabled for this agent: the user must select what it may use under Data sources in the agent's settings."),
            _ => (null, $"tool is required: this agent has several data tools ({string.Join(", ", enabled)}).")
        };
    }

    /// <summary>The interval for a job that names none: the agent's "default job interval" setting.</summary>
    private async Task<int> DefaultIntervalAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await agents.GetAsync(agentId, ct);
        return agent is null ? 5 : DashboardAlertsConfig.Parse(agent.TypeConfigJson).CollectIntervalMinutes;
    }

    public async Task<bool> RequireApprovalAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await agents.GetAsync(agentId, ct);
        return agent is null || DashboardAlertsConfig.Parse(agent.TypeConfigJson).RequireJobApproval;
    }

    /// <summary>Names of datasets this agent's jobs write, whether or not they have run yet, so dashboards and
    /// alert rules can be built against a job that is still waiting for approval.</summary>
    public async Task<IReadOnlySet<string>> DeclaredDatasetsAsync(Guid agentId, CancellationToken ct)
    {
        return DeclaredDatasets(await jobs.GetAllAsync(agentId, ct));
    }

    public static IReadOnlySet<string> DeclaredDatasets(IEnumerable<CollectionJob> all)
    {
        // The platform's own status dataset is always there to build on, before any job has run.
        var names = new HashSet<string>(StringComparer.Ordinal) { DataSourceStatus.DatasetName };
        foreach (var job in all)
        {
            var (dataset, _) = CollectorSpecs.Parse<DatasetSpec>(job.DatasetJson, "dataset");
            if (dataset?.Name is { Length: > 0 } n) names.Add(n);
        }
        return names;
    }

    /// <summary>How long a row of each dataset stays current: three times the longest interval of the jobs that write it
    /// (two missed runs of slack). A dataset written daily is fresh for days; one written every 5 minutes for 15.
    /// Anything that judges "is this data current" (dashboard widgets, alert evaluation) uses this instead of a fixed
    /// cutoff, which would call a healthy 2-hourly or daily dataset stale.</summary>
    public static IReadOnlyDictionary<string, TimeSpan> FreshnessWindows(IEnumerable<CollectionJob> all)
    {
        var windows = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var jobs = all.ToList();
        foreach (var job in jobs)
        {
            var window = TimeSpan.FromMinutes(3.0 * job.IntervalMinutes);
            foreach (var dataset in DeclaredDatasets([job]).Where(d => d != DataSourceStatus.DatasetName))
                windows[dataset] = windows.TryGetValue(dataset, out var existing) && existing > window ? existing : window;
        }
        // The status dataset is written whenever any job runs on a connection, so its cadence is the shortest interval.
        if (jobs.Count > 0) windows[DataSourceStatus.DatasetName] = TimeSpan.FromMinutes(3.0 * jobs.Min(j => j.IntervalMinutes));
        return windows;
    }

    private static string? Clean(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private async Task SaveJobAsync(CollectionJob job, CancellationToken ct)
    {
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureDashboardAgent(Guid agentId, CancellationToken ct)
    {
        if (!await agents.IsDashboardAlertsAgentAsync(agentId, ct)) throw new NotFoundException($"Agent {agentId} not found.");
    }

    private static CollectionJobDto ToDto(CollectionJob j)
    {
        return new CollectionJobDto(
            j.Id, j.Name, j.Description, j.Tool, j.Request, j.IntervalMinutes,
            JsonSerializer.Deserialize<JsonElement>(j.ParserJson), JsonSerializer.Deserialize<JsonElement>(j.DatasetJson),
            string.IsNullOrEmpty(j.TargetsJson) ? null : JsonSerializer.Deserialize<List<string>>(j.TargetsJson),
            j.Status, j.Source, j.LastRunAt, j.LastError, j.LastRowCount, j.CreatedAt, j.UpdatedAt, ApprovalToken(j), j.OutsideAllowlist);
    }
}
