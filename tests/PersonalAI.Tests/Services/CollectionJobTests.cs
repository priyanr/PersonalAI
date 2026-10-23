using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents;
using PersonalAI.Core.Services.Monitoring;
using PersonalAI.Core.Services.Monitoring.Alerts;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Monitoring.Dashboards;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Ssh;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Services;

internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An in-memory app with one Dashboard &amp; Alerts agent monitoring a group with one enabled server (web-1) and
/// one disabled one, a scripted SSH layer that records every command it is asked to run, and a controllable clock.</summary>
internal sealed class JobHarness : IAsyncDisposable
{
    public const string DfOutput = "Filesystem 1B-blocks Used Available Capacity Mounted on\n/dev/sda1 1000 500 500 50% /\n/dev/sdb1 2000 1800 200 90% /mnt/data disk";

    public sealed class ScriptedSsh(Func<SshServer, string, SshCommandResult> respond) : ISshRunner
    {
        public List<string> Commands { get; } = [];
        public Func<SshServer, string, SshCommandResult> Respond { get; set; } = respond;

        public Task<SshCommandResult> RunAsync(SshServer server, string command, TimeSpan timeout, CancellationToken ct)
        {
            lock (Commands) Commands.Add(command);
            return Task.FromResult(Respond(server, command));
        }
    }

    public static SshCommandResult Ok(string stdout) => new(true, 0, stdout, "", null, "SHA256:fp");
    public static SshCommandResult Down(string error = "Connection refused") => new(false, -1, "", "", error, null);

    public ServiceProvider Provider { get; }
    public Guid AgentId { get; private set; }
    public Guid ServerId { get; private set; }
    public ScriptedSsh Ssh { get; }
    public TestTimeProvider Time { get; }
    public ToolConfigCache Cache { get; } = new();
    public CollectionRunner Runner { get; }
    private readonly IReadOnlyDictionary<string, Guid[]>? _extraScopes;
    private readonly bool _scopeSsh;
    private readonly int? _defaultInterval;

    /// <summary>Extra data sources (registered by <paramref name="configure"/>) and the scope items the agent has for them.</summary>
    public JobHarness(Func<SshServer, string, SshCommandResult>? respond = null, bool requireApproval = true, int? defaultInterval = null, AgentType type = AgentType.DashboardAlerts,
        Action<IServiceCollection>? configure = null, IReadOnlyDictionary<string, Guid[]>? extraScopes = null, bool scopeSsh = true)
    {
        _extraScopes = extraScopes;
        _scopeSsh = scopeSsh;
        _defaultInterval = defaultInterval;
        Ssh = new ScriptedSsh(respond ?? ((_, _) => Ok(DfOutput)));
        Time = new TestTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        var db = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddScoped<IAgentsRepository, AgentsRepository>();
        services.AddScoped<ISshServersRepository, SshServersRepository>();
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        services.AddScoped<ISecretsRepository, SecretsRepository>();
        services.AddScoped<IAlertsRepository, AlertsRepository>();
        services.AddScoped<ICollectionJobsRepository, CollectionJobsRepository>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<AlertRuleService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<CollectionJobService>();
        services.AddSingleton<ILogger<AlertEvaluator>>(NullLogger<AlertEvaluator>.Instance);
        services.AddScoped<AlertEvaluator>();
        services.AddSingleton<ISshRunner>(Ssh);
        services.AddSingleton(Cache);
        services.AddSingleton<SshGuardProvider>();
        services.AddScoped<IDataSource, SshDataSource>();
        services.AddScoped<DataSourceRegistry>();
        configure?.Invoke(services);
        Provider = services.BuildServiceProvider();

        Runner = new CollectionRunner(Provider.GetRequiredService<IServiceScopeFactory>(), Time, NullLogger<CollectionRunner>.Instance);
        SeedAsync(requireApproval, type).GetAwaiter().GetResult();
    }

    private async Task SeedAsync(bool requireApproval, AgentType type)
    {
        await using var scope = Provider.CreateAsyncScope();
        var servers = scope.ServiceProvider.GetRequiredService<ISshServersRepository>();
        var group = await servers.CreateGroupAsync("prod", null, default);
        ServerId = (await servers.CreateServerAsync(group.Id, "web-1", "10.0.0.5", 22, "root", "SSH_K", true, default))!.Id;
        await servers.CreateServerAsync(group.Id, "off", "10.0.0.6", 22, "root", "SSH_K", false, default);
        var toolScopes = new Dictionary<string, Guid[]>(_extraScopes ?? new Dictionary<string, Guid[]>());
        if (_scopeSsh) toolScopes["ssh"] = [group.Id];
        var config = JsonSerializer.Serialize(new { toolScopes, requireJobApproval = requireApproval, collectIntervalMinutes = _defaultInterval ?? 5 });
        AgentId = (await scope.ServiceProvider.GetRequiredService<IAgentsRepository>().CreateAsync(
            "mon", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Manual, null, null, null, true, default,
            null, type, config)).Id;
    }

    public AsyncServiceScope Scope() => Provider.CreateAsyncScope();
    public async Task<T> With<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Provider.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task<IReadOnlyList<CollectionJob>> Jobs() => With(sp => sp.GetRequiredService<ICollectionJobsRepository>().GetAllAsync(AgentId, default));
    /// <summary>Approves as the UI does: with the token of the job as it currently is. Null if it could not be approved.</summary>
    public async Task<CollectionJob?> Approve(Guid jobId)
    {
        var job = (await Jobs()).Single(j => j.Id == jobId);
        return (await With(sp => sp.GetRequiredService<CollectionJobService>().ApproveAsync(AgentId, jobId, CollectionJobService.ApprovalToken(job), default))).Job;
    }

    /// <summary>Saves a job and approves it if it is waiting, so it is active. Null if it could not be saved or approved.</summary>
    public async Task<CollectionJob?> Activate(JobInput input)
    {
        var saved = await Save(input);
        if (saved.Job is null) return null;
        return saved.Job.Status == JobStatus.PendingApproval ? await Approve(saved.Job.Id) : saved.Job;
    }

    /// <summary>Two active jobs: "Disk usage" every 5 minutes and "Daily disk" every 1440.</summary>
    public async Task TwoJobs()
    {
        await Activate(DiskJob("Disk usage", datasetName: "disk_usage"));
        await Activate(DiskJob("Daily disk", interval: 1440, datasetName: "disk_daily"));
    }

    public Task<JobSaveResult> Save(JobInput input, string source = JobSources.Llm) =>
        With(sp => sp.GetRequiredService<CollectionJobService>().UpsertByNameAsync(AgentId, input, source, default));
    public Task<IReadOnlyList<DataPointRow>?> Latest(string dataset) =>
        With(sp => sp.GetRequiredService<IDatasetRepository>().QueryAsync(AgentId, dataset, null, null, null, true, 100, default));

    public static ParserSpec DfParser(params (string Name, string? Type)[]? columns) => new("table", 1,
        [.. (columns is { Length: > 0 } ? columns : [("fs", null), ("size", "number"), ("used", "number"), ("avail", "number"), ("pct", "number"), ("mount", null)]).Select(c => new ColumnSpec(c.Name, c.Type))],
        true, null, null, null);

    public static JobInput DiskJob(string name = "Disk usage", string command = "df -P -B1", int interval = 5, List<string>? targets = null, string datasetName = "disk_usage") =>
        new(name, "per-mount disk usage", command, interval, DfParser(), new DatasetSpec(datasetName, "timeseries", ["mount"]), targets);

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}

public class CollectionRunnerTests
{
    [Fact]
    public async Task TwoJobs_RunOnTheEnabledServer_StoreDatasets_PinTheHostKey_AndRecordTheRun()
    {
        await using var h = new JobHarness();
        await h.TwoJobs();

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.Due, null, default);

        result!.JobsRun.Should().Be(2);
        result.Results.Should().HaveCount(2).And.OnlyContain(r => r.Success && r.Target == "web-1"); // the disabled server is skipped
        (await h.Latest("disk_usage"))!.Should().HaveCount(2);
        (await h.Latest("disk_daily"))!.Should().HaveCount(2);
        var jobs = await h.Jobs();
        jobs.Should().OnlyContain(j => j.LastRunAt != null && j.LastError == null && j.LastRowCount > 0);
        (await h.With(sp => sp.GetRequiredService<ISshServersRepository>().GetServerAsync(h.ServerId, default)))!.HostKeyFingerprint.Should().Be("SHA256:fp");
    }

    [Fact]
    public async Task Due_EachJobRunsOnItsOwnInterval_OnTheJobsClock()
    {
        await using var h = new JobHarness();
        await h.TwoJobs(); // one every 5 min, one every 1440

        (await h.Runner.RunAsync(h.AgentId, JobSelection.Due, null, default))!.JobsRun.Should().Be(2);
        (await h.Runner.RunAsync(h.AgentId, JobSelection.Due, null, default))!.JobsRun.Should().Be(0);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        var fiveLater = await h.Runner.RunAsync(h.AgentId, JobSelection.Due, null, default);
        fiveLater!.Results.Select(r => r.Job).Should().OnlyContain(j => j == "Disk usage");

        h.Time.Advance(TimeSpan.FromHours(24));
        (await h.Runner.RunAsync(h.AgentId, JobSelection.Due, null, default))!.JobsRun.Should().Be(2);
    }

    [Fact]
    public async Task RunNow_RunsEveryActiveJob_RegardlessOfInterval()
    {
        await using var h = new JobHarness();
        await h.TwoJobs();
        await h.Runner.RunAsync(h.AgentId, JobSelection.Due, null, default);

        (await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default))!.JobsRun.Should().Be(2);
    }

    [Fact]
    public async Task APendingJob_NeverRuns_AndADisabledOneDoesNot()
    {
        await using var h = new JobHarness(); // approval required
        var saved = await h.Save(JobHarness.DiskJob());
        saved.Job!.Status.Should().Be(JobStatus.PendingApproval);

        (await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default))!.JobsRun.Should().Be(0);
        h.Ssh.Commands.Should().BeEmpty();

        await h.Approve(saved.Job.Id);
        await h.With(sp => sp.GetRequiredService<CollectionJobService>().SetEnabledAsync(h.AgentId, saved.Job.Id, false, default));
        (await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default))!.JobsRun.Should().Be(0);
    }

    [Fact]
    public async Task CustomJob_ParsesRows_AddsTargetAndEntity_AndKeysEntitiesByServerAndKeyFields()
    {
        await using var h = new JobHarness(requireApproval: false);
        (await h.Save(JobHarness.DiskJob())).Job!.Status.Should().Be(JobStatus.Active);

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.Due, null, default);

        result!.Results.Should().ContainSingle().Which.Rows.Should().Be(2);
        var rows = (await h.Latest("disk_usage"))!;
        rows.Select(r => r.EntityKey).Should().BeEquivalentTo($"{h.ServerId}|/", $"{h.ServerId}|/mnt/data disk");
        var data = JsonDocument.Parse(rows.Single(r => r.EntityKey.EndsWith("|/mnt/data disk")).ValuesJson).RootElement;
        data.GetProperty("target").GetString().Should().Be("web-1");
        data.GetProperty("entity").GetString().Should().Be("/mnt/data disk");
        data.GetProperty("pct").GetDouble().Should().Be(90);
        (await h.Jobs()).Single().LastRowCount.Should().Be(2);
    }

    [Fact]
    public async Task CustomJob_ThatParsesNothing_RecordsAnErrorOnTheJob_NotASilentEmptyDataset()
    {
        await using var h = new JobHarness(respond: (_, _) => JobHarness.Ok("nothing useful here"), requireApproval: false);
        await h.Save(JobHarness.DiskJob() with { Parser = JobHarness.DfParser() with { SkipLines = 0 } }, JobSources.User); // user job: no dry run

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Should().ContainSingle().Which.Success.Should().BeFalse();
        (await h.Jobs()).Single().LastError.Should().Contain("web-1").And.Contain("No line had 6 columns");
    }

    [Fact]
    public async Task TargetsFilter_RestrictsAJobToTheServersItNames()
    {
        await using var h = new JobHarness(requireApproval: false);
        await h.Save(JobHarness.DiskJob(targets: ["nobody"]));

        (await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default))!.Results.Should().BeEmpty();

        await h.Save(JobHarness.DiskJob(targets: ["WEB-1"]));
        (await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default))!.Results.Should().ContainSingle();
    }

    [Fact]
    public async Task CommandNoLongerAllowed_IsRefusedAtRunTime_WithoutTouchingTheServer()
    {
        await using var h = new JobHarness(requireApproval: false);
        await h.Save(JobHarness.DiskJob());
        h.Cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "uptime" }); // allowlist tightened afterwards

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Single().Error.Should().Contain("no longer allowed");
        h.Ssh.Commands.Should().BeEmpty();
        (await h.Latest("disk_usage")).Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task UnreachableServer_WritesAvailableFalse_ToTheStatusDataset_ForAnyJob()
    {
        await using var h = new JobHarness(respond: (_, _) => JobHarness.Down("Connection refused"));
        await h.TwoJobs();

        await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        // One point per connection per pass, however many jobs ran on it.
        var status = (await h.Latest(DataSourceStatus.DatasetName))!.Single();
        status.EntityKey.Should().Be(h.ServerId.ToString());
        var data = JsonDocument.Parse(status.ValuesJson).RootElement;
        data.GetProperty("available").GetBoolean().Should().BeFalse();
        data.GetProperty("target").GetString().Should().Be("web-1");
        data.GetProperty("tool").GetString().Should().Be("ssh");
        data.GetProperty("error").GetString().Should().Be("Connection refused");
        (await h.With(sp => sp.GetRequiredService<ISshServersRepository>().GetServerAsync(h.ServerId, default)))!.LastError.Should().Be("Connection refused");
    }

    [Fact]
    public async Task AFailingCommand_OnAServerThatAnswered_DoesNotMakeItLookDown()
    {
        // The server answers; only one command times out (a slow systemctl). That is a job problem, not an outage.
        await using var h = new JobHarness(respond: (_, cmd) =>
            cmd == "uptime" ? new SshCommandResult(true, 124, "", "timed out", null, "SHA256:fp") : JobHarness.Ok(JobHarness.DfOutput));
        await h.Activate(JobHarness.DiskJob());
        await h.Activate(JobHarness.DiskJob("Slow", command: "uptime", datasetName: "slow"));

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Single(r => r.Job == "Slow").Success.Should().BeFalse();
        result.Results.Single(r => r.Job == "Disk usage").Success.Should().BeTrue();
        JsonDocument.Parse((await h.Latest(DataSourceStatus.DatasetName))!.Single().ValuesJson).RootElement
            .GetProperty("available").GetBoolean().Should().BeTrue();
        (await h.Jobs()).Single(j => j.Name == "Slow").LastError.Should().Contain("failed with code 124");
    }

    [Fact]
    public async Task ANonDashboardAgent_ReturnsNull()
    {
        await using var h = new JobHarness(type: AgentType.General);

        (await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default)).Should().BeNull();
    }

    [Fact]
    public async Task AConcurrentPass_IsSkipped_NotRunTwice()
    {
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        await using var h = new JobHarness(respond: (_, cmd) =>
        {
            started.TrySetResult();
            gate.Task.GetAwaiter().GetResult();
            return JobHarness.Ok(JobHarness.DfOutput);
        });
        await h.Activate(JobHarness.DiskJob());

        var first = Task.Run(() => h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default));
        await started.Task;
        var second = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);
        gate.SetResult();

        second!.Skipped.Should().BeTrue();
        (await first)!.Skipped.Should().BeFalse();
    }

    [Fact]
    public async Task APass_EvaluatesAlertRules_SoADownServerRaisesAnAlert_EvenBeforeAnyDataExists()
    {
        await using var h = new JobHarness(respond: (_, _) => JobHarness.Down("Connection refused"));
        await h.Activate(JobHarness.DiskJob());
        // The rule is created before the first run: the platform's status dataset is always declared.
        var rule = await h.With(sp => sp.GetRequiredService<AlertRuleService>().CreateAsync(h.AgentId,
            new("Server down", null, DataSourceStatus.DatasetName, "available", "eq", 0, 0, "critical", null, null, true), AlertSources.User, default));
        rule.Errors.Should().BeEmpty();

        await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        var events = await h.With(sp => sp.GetRequiredService<IAlertsRepository>().GetEventsAsync(h.AgentId, true, null, default));
        events.Should().ContainSingle().Which.Message.Should().Be("web-1: available is false");
    }

    [Fact]
    public void BuildPoints_KeysEntitiesByKeyFields_AndSkipsRowsMissingOne()
    {
        var target = new DataConnection(Guid.NewGuid(), "web-1");
        var rows = new[] { System.Text.Json.Nodes.JsonNode.Parse("""{"namespace":"kube-system","name":"dns","restarts":2}""")!.AsObject(),
                           System.Text.Json.Nodes.JsonNode.Parse("""{"name":"orphan"}""")!.AsObject() };

        var points = CollectionRunner.BuildPoints(new DatasetSpec("pods", "snapshot", ["namespace", "name"]), target, rows, DateTime.UtcNow);

        var p = points.Should().ContainSingle().Subject;
        p.Kind.Should().Be(DatasetKind.Snapshot);
        p.EntityKey.Should().Be($"{target.Id}|kube-system/dns");
        p.ValuesJson.Should().Contain("\"entity\":\"kube-system/dns\"").And.Contain("\"target\":\"web-1\"").And.Contain("\"restarts\":2");
    }
}

public class CollectionRunnerTestAndAdHocTests
{
    [Fact]
    public async Task Test_ValidationFailures_NeverReachTheServer()
    {
        await using var h = new JobHarness();

        var outcome = await h.Runner.TestAsync(h.AgentId, JobHarness.DiskJob(command: "df; rm -rf /"), null, default);

        outcome.Success.Should().BeFalse();
        outcome.ValidationErrors.Should().ContainSingle(e => e.StartsWith("command:") && e.Contains("chained"));
        h.Ssh.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_Success_ReturnsSampleRowsAndCount_AndSavesNothing()
    {
        await using var h = new JobHarness();

        var outcome = await h.Runner.TestAsync(h.AgentId, JobHarness.DiskJob(), null, default);

        outcome.Success.Should().BeTrue();
        outcome.Target.Should().Be("web-1");
        outcome.RowCount.Should().Be(2);
        outcome.SampleRows[0]["mount"]!.GetValue<string>().Should().Be("/");
        (await h.Jobs()).Should().BeEmpty();
        (await h.Latest("disk_usage")).Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Test_ParserThatMatchesNothing_ExplainsWithTheStartOfTheOutput()
    {
        await using var h = new JobHarness(respond: (_, _) => JobHarness.Ok("error: not found"));

        var outcome = await h.Runner.TestAsync(h.AgentId, JobHarness.DiskJob(), null, default);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("error: not found");
    }

    [Fact]
    public async Task Test_ServerFailure_IsReported()
    {
        await using var h = new JobHarness(respond: (_, _) => JobHarness.Down("Permission denied (publickey)."));

        var outcome = await h.Runner.TestAsync(h.AgentId, JobHarness.DiskJob(), null, default);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("Permission denied");
    }

    [Fact]
    public async Task Test_IgnoresNameClashes_ButNotUnknownServers()
    {
        await using var h = new JobHarness(requireApproval: false);
        await h.Save(JobHarness.DiskJob());

        (await h.Runner.TestAsync(h.AgentId, JobHarness.DiskJob(), null, default)).Success.Should().BeTrue();

        var unknown = await h.Runner.TestAsync(h.AgentId, JobHarness.DiskJob(), "ghost", default);
        unknown.Success.Should().BeFalse();
        unknown.Error.Should().Be("No connection named 'ghost'. This agent can use: web-1.");
    }

    [Fact]
    public async Task AdHoc_AllowedCommand_ReturnsOutput_ADeniedOneNeverConnects()
    {
        await using var h = new JobHarness(respond: (_, _) => JobHarness.Ok("up 3 days"));

        var ok = await h.Runner.RunCommandAsync(h.AgentId, "ssh", null, "uptime", default);
        ok.Success.Should().BeTrue();
        ok.Output.Should().Be("up 3 days");

        h.Ssh.Commands.Clear();
        var denied = await h.Runner.RunCommandAsync(h.AgentId, "ssh", null, "uptime; reboot", default);
        denied.Success.Should().BeFalse();
        denied.Error.Should().Contain("not allowed");
        h.Ssh.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task AdHoc_NonZeroExit_ReportsStderrAndCode()
    {
        await using var h = new JobHarness(respond: (_, _) => new SshCommandResult(true, 1, "partial", "boom", null, null));

        var result = await h.Runner.RunCommandAsync(h.AgentId, "ssh", "web-1", "df", default);

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("partial").And.Contain("[stderr] boom");
        result.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task AdHoc_OnlyForDashboardAgents()
    {
        await using var h = new JobHarness(type: AgentType.General);

        (await h.Runner.RunCommandAsync(h.AgentId, "ssh", null, "uptime", default)).Error.Should().Contain("only available to Dashboard");
    }
}

public class CollectionJobServiceTests
{
    [Fact]
    public async Task NewModelJob_WaitsForApproval_ByDefault_AndIsActiveWhenApprovalIsOff()
    {
        await using var needs = new JobHarness();
        (await needs.Save(JobHarness.DiskJob())).Job!.Status.Should().Be(JobStatus.PendingApproval);

        await using var free = new JobHarness(requireApproval: false);
        (await free.Save(JobHarness.DiskJob())).Job!.Status.Should().Be(JobStatus.Active);
    }

    [Fact]
    public async Task Validation_ReportsEveryProblem_WithGuardRefusalsFirstClass()
    {
        await using var h = new JobHarness();

        var bad = await h.Save(new("", null, "df > /tmp/x", 0, new ParserSpec("table", null, null, null, null, null, null),
            new DatasetSpec("Bad Name", "weird", ["missing"]), null));

        bad.IsValid.Should().BeFalse();
        bad.Errors.Should().Contain(e => e.Contains("name is required"))
            .And.Contain(e => e.StartsWith("command:") && e.Contains("not allowed"))
            .And.Contain(e => e.Contains("intervalMinutes"))
            .And.Contain(e => e.Contains("parser.columns is required"))
            .And.Contain(e => e.Contains("dataset.name"))
            .And.Contain(e => e.Contains("dataset.kind"));
        (await h.Jobs()).Should().BeEmpty();
    }

    [Fact]
    public async Task KeyFields_MustBeFieldsTheParserProduces()
    {
        await using var h = new JobHarness();

        var bad = await h.Save(JobHarness.DiskJob() with { Dataset = new DatasetSpec("disk_usage", null, ["drive"]) });

        bad.Errors.Should().ContainSingle(e => e.Contains("'drive' is not a field the parser produces") && e.Contains("mount"));
    }

    [Fact]
    public async Task ReservedFieldNames_AreRefused_BecauseThePlatformAddsThem()
    {
        await using var h = new JobHarness();

        var bad = await h.Save(JobHarness.DiskJob() with { Parser = JobHarness.DfParser(("fs", null), ("target", null), ("entity", null)) });

        bad.Errors.Should().Contain(e => e.Contains("'target' is reserved")).And.Contain(e => e.Contains("'entity' is reserved"));
    }

    [Fact]
    public async Task ThereIsNoBuiltinParser()
    {
        await using var h = new JobHarness();

        var bad = await h.Save(JobHarness.DiskJob() with { Parser = new ParserSpec("builtin", null, null, null, null, null, null) });

        bad.Errors.Should().Contain(e => e.Contains("parser.type must be one of"));
    }

    [Fact]
    public async Task Names_AreUniqueCaseInsensitively_AndJobCountIsCapped()
    {
        await using var h = new JobHarness(requireApproval: false);
        await h.Save(JobHarness.DiskJob("Disks", datasetName: "d0"));

        (await h.Save(JobHarness.DiskJob("DISKS", command: "uptime"))).Job!.Request.Should().Be("uptime"); // same name, any case: the job is updated, not duplicated
        (await h.Jobs()).Should().ContainSingle();

        for (var i = 1; i < CollectionJobService.MaxJobs; i++)
            (await h.Save(JobHarness.DiskJob($"job{i}", datasetName: $"d{i}"))).IsValid.Should().BeTrue();
        (await h.Save(JobHarness.DiskJob("one-too-many", datasetName: "dx"))).Errors.Should().ContainSingle(e => e.Contains("at most 20"));
    }

    [Fact]
    public async Task AJobWithNoInterval_GetsTheAgentsDefaultJobInterval_AndAnExplicitOneWins()
    {
        await using var h = new JobHarness(requireApproval: false, defaultInterval: 2);

        (await h.Save(JobHarness.DiskJob("Default") with { IntervalMinutes = null })).Job!.IntervalMinutes.Should().Be(2);
        (await h.Save(JobHarness.DiskJob("Explicit", interval: 10, datasetName: "other"))).Job!.IntervalMinutes.Should().Be(10);
    }

    [Fact]
    public async Task EditingWhatRuns_SendsAnActiveJobBackForApproval_ButAnIntervalOrDescriptionChangeDoesNot()
    {
        await using var h = new JobHarness();
        var created = (await h.Save(JobHarness.DiskJob())).Job!;
        await h.Approve(created.Id);

        var harmless = await h.Save(JobHarness.DiskJob(interval: 10) with { Description = "renamed description" });
        harmless.Job!.Status.Should().Be(JobStatus.Active);
        harmless.Job.IntervalMinutes.Should().Be(10);

        (await h.Save(JobHarness.DiskJob(command: "df -h"))).Job!.Status.Should().Be(JobStatus.PendingApproval);
        (await h.Jobs()).Single().Status.Should().Be(JobStatus.PendingApproval);
    }

    [Fact]
    public async Task AModel_CannotTouchTheUsersJobs()
    {
        await using var h = new JobHarness();
        await h.Save(JobHarness.DiskJob("Mine"), JobSources.User);

        var edit = await h.Save(JobHarness.DiskJob("Mine", command: "free"));

        edit.Errors.Single().Should().Contain("created by the user");
        (await h.With(sp => sp.GetRequiredService<CollectionJobService>().DeleteByNameAsync(h.AgentId, "Mine", JobSources.Llm, default)))
            .Should().Contain("created by the user");
        (await h.With(sp => sp.GetRequiredService<CollectionJobService>().DeleteByNameAsync(h.AgentId, "nope", JobSources.Llm, default)))
            .Should().Contain("No job named");
    }

    [Fact]
    public async Task Approve_ActivatesAPendingJob_ButOnlyIfItsCommandIsStillAllowed()
    {
        await using var h = new JobHarness();
        var job = (await h.Save(JobHarness.DiskJob())).Job!;
        var svc = (Func<Task<CollectionJob?>>)(() => h.Approve(job.Id));

        h.Cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "uptime" });
        (await svc()).Should().BeNull();
        (await h.Jobs()).Single().Status.Should().Be(JobStatus.PendingApproval);

        h.Cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "df" });
        (await svc())!.Status.Should().Be(JobStatus.Active);
        (await svc()).Should().BeNull(); // already approved
    }

    [Fact]
    public async Task Enable_CannotBeUsedToBypassApproval()
    {
        await using var h = new JobHarness();
        var job = (await h.Save(JobHarness.DiskJob())).Job!;
        var svc = h.Provider;

        (await h.With(sp => sp.GetRequiredService<CollectionJobService>().SetEnabledAsync(h.AgentId, job.Id, true, default))).Should().BeNull();

        await h.Approve(job.Id);
        (await h.With(sp => sp.GetRequiredService<CollectionJobService>().SetEnabledAsync(h.AgentId, job.Id, false, default)))!.Status.Should().Be(JobStatus.Disabled);
        (await h.With(sp => sp.GetRequiredService<CollectionJobService>().SetEnabledAsync(h.AgentId, job.Id, true, default)))!.Status.Should().Be(JobStatus.Active);
        svc.Should().NotBeNull();
    }

    [Fact]
    public async Task DeclaredDatasets_CoverJobsAndTheStatusDataset_EvenBeforeTheyHaveRun()
    {
        await using var h = new JobHarness();
        await h.Save(JobHarness.DiskJob());

        var declared = await h.With(sp => sp.GetRequiredService<CollectionJobService>().DeclaredDatasetsAsync(h.AgentId, default));

        declared.Should().BeEquivalentTo(DataSourceStatus.DatasetName, "disk_usage");
    }

    [Fact]
    public async Task ADashboardAndAnAlertRule_CanBeBuiltOnADatasetAPendingJobDeclares()
    {
        await using var h = new JobHarness();
        await h.Save(JobHarness.DiskJob()); // pending: nothing collected yet

        var dashboard = await h.With(sp => sp.GetRequiredService<DashboardService>().ValidateAsync(h.AgentId,
            """{"title":"T","widgets":[{"id":"d","type":"bar","query":{"dataset":"disk_usage","field":"pct"}}]}""", default));
        var rule = await h.With(sp => sp.GetRequiredService<AlertRuleService>().CreateAsync(h.AgentId,
            new("Disk full", null, "disk_usage", "pct", "gte", 90, 0, "critical", null, null, true), AlertSources.User, default));
        var unknown = await h.With(sp => sp.GetRequiredService<DashboardService>().ValidateAsync(h.AgentId,
            """{"title":"T","widgets":[{"id":"d","type":"bar","query":{"dataset":"nope","field":"pct"}}]}""", default));

        dashboard.IsValid.Should().BeTrue(string.Join("; ", dashboard.Errors));
        rule.IsValid.Should().BeTrue(string.Join("; ", rule.Errors));
        unknown.Errors.Should().ContainSingle(e => e.Contains("no dataset 'nope'") && e.Contains("disk_usage"));
    }
}

public class SshGuardProviderTests
{
    [Fact]
    public void ReadsTheToolSetting_AppliesEditsImmediately_AndBlankMeansDefaults()
    {
        var cache = new ToolConfigCache();
        var provider = new SshGuardProvider(cache);

        provider.Current.Check("free").Allowed.Should().BeTrue(); // nothing set: defaults

        cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "df\nuptime" });
        provider.Current.Check("df -h").Allowed.Should().BeTrue();
        provider.Current.Check("free").Allowed.Should().BeFalse();

        cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "free" });
        provider.Current.Check("free").Allowed.Should().BeTrue();
        provider.Current.Check("df").Allowed.Should().BeFalse();

        cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "  " });
        provider.Current.Check("df").Allowed.Should().BeTrue();
    }

    [Fact]
    public void TheSameTextReusesTheSameGuard()
    {
        var cache = new ToolConfigCache();
        cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "df" });
        var provider = new SshGuardProvider(cache);

        provider.Current.Should().BeSameAs(provider.Current);
    }
}

public class CollectorToolTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
    private static ToolExecutionContext Ctx(Guid agent) => new(false, AgentId: agent);

    private const string SaveJson = """
        {"name":"Disk usage","request":"df -P -B1","intervalMinutes":5,
         "parser":{"type":"table","skipLines":1,"lastColumnTakesRest":true,
                   "columns":[{"name":"fs"},{"name":"size","type":"number"},{"name":"used","type":"number"},{"name":"avail","type":"number"},{"name":"pct","type":"number"},{"name":"mount"}]},
         "dataset":{"name":"disk_usage","kind":"timeseries","keyFields":["mount"]}}
        """;

    [Fact]
    public async Task Tools_AreAgentOnly_AndRefuseWithoutAnAgent()
    {
        await using var h = new JobHarness();
        var scopes = h.Provider.GetRequiredService<IServiceScopeFactory>();
        ITool[] tools = [new SshTool(h.Runner), new CollectorTestTool(h.Runner),
            new CollectorSaveTool(scopes, h.Runner), new CollectorListTool(scopes), new CollectorDeleteTool(scopes), new DataQueryTool(scopes)];

        foreach (var tool in tools)
        {
            tool.RequiresAgentRun.Should().BeTrue(tool.Name);
            var result = (ToolCompleted)await tool.ExecuteAsync(J("{}"), new ToolExecutionContext(false), default);
            result.IsError.Should().BeTrue(tool.Name);
            result.Content.Should().Contain("only available to Dashboard");
        }
    }

    [Fact]
    public async Task AgentOnlyTools_AreHiddenFromChatDefinitions_ButOtherToolsStay()
    {
        await using var h = new JobHarness();
        var scopes = h.Provider.GetRequiredService<IServiceScopeFactory>();
        var registry = new ToolRegistry([new DataQueryTool(scopes), new CollectorListTool(scopes), new PlainTool()]);

        registry.Definitions.Select(d => d.Name).Should().BeEquivalentTo("data_query", "collector_list", "plain");
        registry.ChatDefinitions.Select(d => d.Name).Should().Equal("plain");
    }

    private sealed class PlainTool : ITool
    {
        public string Name => "plain";
        public string Description => "x";
        public string ParametersSchemaJson => "{}";
        public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct) =>
            Task.FromResult<ToolExecutionResult>(new ToolCompleted("ok"));
    }

    [Fact]
    public async Task SshTool_RunsAllowedCommands_AndExplainsRefusals()
    {
        await using var h = new JobHarness(respond: (_, _) => JobHarness.Ok("up 3 days"));
        var tool = new SshTool(h.Runner);

        var ok = (ToolCompleted)await tool.ExecuteAsync(J("""{"command":"uptime"}"""), Ctx(h.AgentId), default);
        ok.IsError.Should().BeFalse();
        ok.Content.Should().Be("up 3 days");

        var denied = (ToolCompleted)await tool.ExecuteAsync(J("""{"command":"rm -rf /"}"""), Ctx(h.AgentId), default);
        denied.IsError.Should().BeTrue();
        denied.Content.Should().Contain("not on the allowed command list").And.Contain("kubectl get");

        (await tool.ExecuteAsync(J("{}"), Ctx(h.AgentId), default)).Should().BeOfType<ToolCompleted>().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public void SshTool_DeclaresItsSettings_TheAllowlistAndTheServersComponent()
    {
        var tool = new SshTool(null!);

        tool.SettingsFields.Select(f => (f.Name, f.Type)).Should().Equal(
            ("AllowedCommands", ToolSettingFieldType.TextArea), ("Servers", ToolSettingFieldType.Custom));
        tool.SettingsFields[0].DefaultValue.Should().Contain("kubectl get").And.NotContain("sudo").And.NotContain("awk");
        tool.SettingsFields[1].CustomComponentKey.Should().Be("sshServers");
    }

    [Fact]
    public async Task CollectorSave_DryRunsFirst_RefusesWhatDoesNotWork_AndSavesPendingWhatDoes()
    {
        await using var h = new JobHarness();
        var scopes = h.Provider.GetRequiredService<IServiceScopeFactory>();
        var save = new CollectorSaveTool(scopes, h.Runner);

        // 1. a command the guard refuses: nothing saved, nothing run
        var refused = (ToolCompleted)await save.ExecuteAsync(J(SaveJson.Replace("df -P -B1", "df > x")), Ctx(h.AgentId), default);
        refused.IsError.Should().BeTrue();
        refused.Content.Should().Contain("Not saved").And.Contain("not allowed");
        h.Ssh.Commands.Should().BeEmpty();

        // 2. a parser that produces nothing from the real output
        h.Ssh.Respond = (_, _) => JobHarness.Ok("garbage");
        var empty = (ToolCompleted)await save.ExecuteAsync(J(SaveJson), Ctx(h.AgentId), default);
        empty.IsError.Should().BeTrue();
        empty.Content.Should().Contain("Not saved").And.Contain("garbage");
        (await h.Jobs()).Should().BeEmpty();

        // 3. it works: saved, and waiting for the user's approval
        h.Ssh.Respond = (_, _) => JobHarness.Ok(JobHarness.DfOutput);
        var good = (ToolCompleted)await save.ExecuteAsync(J(SaveJson), Ctx(h.AgentId), default);
        good.IsError.Should().BeFalse();
        good.Content.Should().Contain("Created job 'Disk usage'").And.Contain("waiting for the user's approval").And.Contain("2 row(s)").And.Contain("\"mount\":\"/\"");
        (await h.Jobs()).Single().Status.Should().Be(JobStatus.PendingApproval);
    }

    [Fact]
    public async Task CollectorSave_RejectsTyposInTheSpecByName()
    {
        await using var h = new JobHarness();
        var save = new CollectorSaveTool(h.Provider.GetRequiredService<IServiceScopeFactory>(), h.Runner);

        var result = (ToolCompleted)await save.ExecuteAsync(J(SaveJson.Replace("skipLines", "skipLine")), Ctx(h.AgentId), default);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("skipLine");
    }

    [Fact]
    public async Task CollectorTestListAndDelete_RoundTrip()
    {
        await using var h = new JobHarness(requireApproval: false);
        var scopes = h.Provider.GetRequiredService<IServiceScopeFactory>();
        var ctx = Ctx(h.AgentId);

        var test = (ToolCompleted)await new CollectorTestTool(h.Runner).ExecuteAsync(J(SaveJson), ctx, default);
        test.IsError.Should().BeFalse();
        test.Content.Should().StartWith("OK on 'web-1': 2 row(s) parsed");
        (await h.Jobs()).Should().BeEmpty(); // a test saves nothing

        await new CollectorSaveTool(scopes, h.Runner).ExecuteAsync(J(SaveJson), ctx, default);
        var list = (ToolCompleted)await new CollectorListTool(scopes).ExecuteAsync(J("{}"), ctx, default);
        list.Content.Should().Contain("Disk usage [active, llm]: every 5 min: df -P -B1").And.Contain("dataset: disk_usage (timeseries), key mount").And.Contain("last run: never");

        await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);
        (await new CollectorListTool(scopes).ExecuteAsync(J("{}"), ctx, default) as ToolCompleted)!.Content.Should().Contain("2 row(s)");

        var deleted = (ToolCompleted)await new CollectorDeleteTool(scopes).ExecuteAsync(J("""{"name":"Disk usage"}"""), ctx, default);
        deleted.IsError.Should().BeFalse();
        (await h.Jobs()).Should().BeEmpty();
    }

    [Fact]
    public async Task CollectorList_WhenEmpty_SaysSo_AndDeleteRefusesTheUsersJobs()
    {
        await using var h = new JobHarness();
        var scopes = h.Provider.GetRequiredService<IServiceScopeFactory>();

        (await new CollectorListTool(scopes).ExecuteAsync(J("{}"), Ctx(h.AgentId), default) as ToolCompleted)!.Content.Should().Contain("No jobs yet");

        await h.Save(JobHarness.DiskJob("Theirs"), JobSources.User);
        var refused = (ToolCompleted)await new CollectorDeleteTool(scopes).ExecuteAsync(J("""{"name":"Theirs"}"""), Ctx(h.AgentId), default);
        refused.IsError.Should().BeTrue();
        refused.Content.Should().Contain("created by the user");
    }
}

/// <summary>A command that is merely not on the allowed list is not refused: the job waits for the user to approve that exact
/// text, and runs only after. Anything the guard refuses outright (chaining, redirects) is never approvable.</summary>
public class OutsideAllowlistTests
{
    private const string Unlisted = "ls /var/log";

    [Fact]
    public async Task ACommandNotOnTheList_IsSavedPendingAndFlagged_EvenWhenApprovalIsOff()
    {
        await using var h = new JobHarness(requireApproval: false);

        var saved = await h.Save(JobHarness.DiskJob(command: Unlisted));

        saved.Errors.Should().BeEmpty();
        saved.Job!.Status.Should().Be(JobStatus.PendingApproval);
        saved.Job.OutsideAllowlist.Should().BeTrue();
        (await h.Save(JobHarness.DiskJob("Listed"))).Job!.Status.Should().Be(JobStatus.Active); // the setting still governs listed ones
    }

    [Theory]
    [InlineData("ls /var/log; rm -rf /")]
    [InlineData("ls > /tmp/x")]
    [InlineData("cat $(whoami)")]
    [InlineData("kubectl get secrets")]
    [InlineData("kubectl get pods --kubeconfig=/x")]
    [InlineData("ls /var/log | grep -r x")]
    public async Task AHardRefusal_IsNeverSaved_NotEvenForApproval(string command)
    {
        await using var h = new JobHarness();

        var saved = await h.Save(JobHarness.DiskJob(command: command));

        saved.Job.Should().BeNull();
        saved.Errors.Should().ContainSingle(e => e.StartsWith("command:"));
    }

    [Fact]
    public async Task NothingRunsBeforeApproval_ThenTheApprovedCommandRunsExactlyAsWritten()
    {
        await using var h = new JobHarness();
        var job = (await h.Save(JobHarness.DiskJob(command: Unlisted))).Job!;

        (await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default))!.JobsRun.Should().Be(0);
        h.Ssh.Commands.Should().BeEmpty();

        (await h.Approve(job.Id))!.Status.Should().Be(JobStatus.Active);
        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Should().ContainSingle().Which.Success.Should().BeTrue();
        h.Ssh.Commands.Should().Equal(Unlisted);
    }

    [Fact]
    public async Task TheFlagIsPartOfWhatIsApproved_SoTheApprovalTokenChangesWithIt()
    {
        await using var h = new JobHarness();
        var job = (await h.Save(JobHarness.DiskJob(command: Unlisted))).Job!;
        var token = CollectionJobService.ApprovalToken(job);

        job.OutsideAllowlist = false;

        CollectionJobService.ApprovalToken(job).Should().NotBe(token);
    }

    [Fact]
    public async Task EditingTheCommand_MovesTheFlagAndSendsTheJobBackForApproval()
    {
        await using var h = new JobHarness();
        var job = (await h.Save(JobHarness.DiskJob())).Job!;
        await h.Approve(job.Id);
        job = (await h.Jobs()).Single();
        job.OutsideAllowlist.Should().BeFalse();

        var toUnlisted = (await h.Save(JobHarness.DiskJob(command: Unlisted))).Job!;
        toUnlisted.Status.Should().Be(JobStatus.PendingApproval);
        toUnlisted.OutsideAllowlist.Should().BeTrue();

        var back = (await h.Save(JobHarness.DiskJob())).Job!;
        back.Status.Should().Be(JobStatus.PendingApproval);
        back.OutsideAllowlist.Should().BeFalse();
    }

    [Fact]
    public async Task AnActiveJob_WhoseFlagWasLost_IsRefusedAtRunTime()
    {
        await using var h = new JobHarness();
        var job = (await h.Save(JobHarness.DiskJob(command: Unlisted))).Job!;
        await h.Approve(job.Id);
        await h.With(async sp =>
        {
            var repo = sp.GetRequiredService<ICollectionJobsRepository>();
            var stored = (await repo.GetAsync(h.AgentId, job.Id, default))!;
            stored.OutsideAllowlist = false; // e.g. the row was edited behind the app's back
            await repo.SaveAsync(stored, default);
            return 0;
        });

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Single().Error.Should().Contain("no longer allowed");
        h.Ssh.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ADryRun_NeverExecutesAnUnlistedCommand()
    {
        await using var h = new JobHarness();

        var outcome = await h.Runner.TestAsync(h.AgentId, JobHarness.DiskJob(command: Unlisted), null, default);

        outcome.Success.Should().BeFalse();
        outcome.NeedsApproval.Should().BeTrue();
        outcome.ValidationErrors.Should().BeEmpty();
        h.Ssh.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAdHocTool_RefusesAnUnlistedCommand_AndPointsAtJobs()
    {
        await using var h = new JobHarness();

        var result = await h.Runner.RunCommandAsync(h.AgentId, "ssh", null, Unlisted, default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not on the allowed command list").And.Contain("collector_save");
        h.Ssh.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task CollectorSave_SavesAnUnlistedCommandForApproval_WithoutRunningIt()
    {
        await using var h = new JobHarness(requireApproval: false);
        var save = new CollectorSaveTool(h.Provider.GetRequiredService<IServiceScopeFactory>(), h.Runner);
        var json = """
            {"name":"Logs","request":"ls /var/log","parser":{"type":"table","columns":[{"name":"file"}]},"dataset":{"name":"logs","kind":"snapshot","keyFields":["file"]}}
            """;

        var result = (ToolCompleted)await save.ExecuteAsync(JsonDocument.Parse(json).RootElement, new ToolExecutionContext(false, AgentId: h.AgentId), default);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("waiting for the user's approval").And.Contain("not on the tool's allowed list");
        (await h.Jobs()).Single().OutsideAllowlist.Should().BeTrue();
        h.Ssh.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnlistedCommand_IsStillApprovable_AfterTheListChanges()
    {
        await using var h = new JobHarness();
        var job = (await h.Save(JobHarness.DiskJob(command: Unlisted))).Job!;
        h.Cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "uptime" }); // ls is still merely "not listed"

        (await h.Approve(job.Id))!.OutsideAllowlist.Should().BeTrue(); // approvable as written
    }

    [Fact]
    public async Task EveryConnectionIsRecordedAsAvailable_WhenTheServerAnswers()
    {
        await using var h = new JobHarness();
        await h.TwoJobs();

        await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        var status = JsonDocument.Parse((await h.Latest(DataSourceStatus.DatasetName))!.Single().ValuesJson).RootElement;
        status.GetProperty("available").GetBoolean().Should().BeTrue();
        status.TryGetProperty("error", out _).Should().BeFalse();
    }
}
