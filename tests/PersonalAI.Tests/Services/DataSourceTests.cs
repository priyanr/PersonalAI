using PersonalAI.Core.Infrastructure;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Ssh;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Services;

/// <summary>A second data source with a different shape from SSH, to prove nothing in the runner, job service or tools is
/// SSH-specific: no groups (every connection is its own scope item), requests are queries guarded as read-only, and the
/// connections are databases.</summary>
internal sealed class FakeSqlSource : IDataSource
{
    public static readonly Guid Warehouse = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid Analytics = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    public List<(Guid Connection, string Request)> Calls { get; } = [];
    public string Output { get; set; } = "paid 12\nrefunded 3";

    public string ToolName => "sql";
    public string DisplayName => "SQL";
    public string RequestNoun => "query";
    public string ConnectionNoun => "database";

    private static readonly (Guid Id, string Name)[] All = [(Warehouse, "warehouse"), (Analytics, "analytics")];

    public Task<IReadOnlyList<ScopeItem>> ListScopeItemsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ScopeItem>>([.. All.Select(c => new ScopeItem(c.Id, c.Name, "connection", [c.Name]))]);

    public Task<IReadOnlyList<DataConnection>> ResolveAsync(IReadOnlyCollection<Guid> scopeItemIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DataConnection>>([.. All.Where(c => scopeItemIds.Contains(c.Id)).Select(c => new DataConnection(c.Id, c.Name))]);

    public GuardResult Check(string request) =>
        request.TrimStart().StartsWith("select ", StringComparison.OrdinalIgnoreCase) && !request.Contains(';')
            ? GuardResult.Ok
            : GuardResult.Deny("Only a single SELECT statement is allowed.");

    public Task<DataSourceResult> RunAsync(Guid connectionId, string request, TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls) Calls.Add((connectionId, request));
        return Task.FromResult(new DataSourceResult(true, 0, Output, "", null));
    }
}

public class GenericDataSourceTests
{
    private static JobInput OrdersJob(string name = "Orders by status", string? tool = "sql", string command = "select kind, n from orders") =>
        new(name, "orders", command, 5,
            new ParserSpec("table", 0, [new("kind", null), new("n", "number")], null, null, null, null),
            new DatasetSpec("orders_by_kind", "snapshot", ["kind"]), null, tool);

    private static JobHarness Harness(FakeSqlSource sql, bool ssh = true, Guid[]? sqlScope = null) =>
        new(requireApproval: false, configure: s => s.AddSingleton<IDataSource>(sql),
            extraScopes: sqlScope is null ? null : new Dictionary<string, Guid[]> { ["sql"] = sqlScope }, scopeSsh: ssh);

    [Fact]
    public async Task ASqlJob_RunsOnTheSqlConnectionsInScope_AndNeverOnSshServers()
    {
        var sql = new FakeSqlSource();
        await using var h = Harness(sql, sqlScope: [FakeSqlSource.Warehouse]);
        (await h.Save(OrdersJob())).IsValid.Should().BeTrue();

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Should().ContainSingle().Which.Target.Should().Be("warehouse");
        sql.Calls.Should().ContainSingle().Which.Request.Should().Be("select kind, n from orders");
        h.Ssh.Commands.Should().BeEmpty();
        var rows = (await h.Latest("orders_by_kind"))!;
        rows.Select(r => r.EntityKey).Should().BeEquivalentTo($"{FakeSqlSource.Warehouse}|paid", $"{FakeSqlSource.Warehouse}|refunded");
        rows.First().ValuesJson.Should().Contain("\"target\":\"warehouse\"");
    }

    [Fact]
    public async Task TheScopeIsTheBoundary_AConnectionNotSelectedIsNeverReached()
    {
        var sql = new FakeSqlSource();
        await using var h = Harness(sql, sqlScope: [FakeSqlSource.Analytics]); // warehouse is not selected
        await h.Save(OrdersJob());

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Should().ContainSingle().Which.Target.Should().Be("analytics");
        sql.Calls.Should().OnlyContain(c => c.Connection == FakeSqlSource.Analytics);

        // and a job that names a connection outside the scope has nothing to run on
        await h.Save(OrdersJob("Warehouse only") with { Targets = ["warehouse"], Dataset = new DatasetSpec("wh_orders", "snapshot", ["kind"]) });
        sql.Calls.Clear();
        var outside = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);
        sql.Calls.Should().OnlyContain(c => c.Connection == FakeSqlSource.Analytics);
        (await h.Jobs()).Single(j => j.Name == "Warehouse only").LastError.Should().Contain("No enabled connection in this agent's scope");
        outside.Should().NotBeNull();
    }

    [Fact]
    public async Task EachToolsOwnGuardDecides_AndErrorsNameTheToolsRequestNoun()
    {
        var sql = new FakeSqlSource();
        await using var h = Harness(sql, sqlScope: [FakeSqlSource.Warehouse]);

        var drop = await h.Save(OrdersJob(command: "drop table orders"));
        var chained = await h.Save(OrdersJob(command: "select 1; select 2"));
        var sshOnSql = await h.Save(OrdersJob(tool: "ssh", command: "select kind, n from orders")); // a query is not an allowlisted command

        drop.Errors.Should().Contain(e => e.StartsWith("query:") && e.Contains("single SELECT"));
        chained.Errors.Should().Contain(e => e.StartsWith("query:"));
        // SSH does not refuse a query outright: it is merely not on its allowed list, so it can be saved for approval as written.
        sshOnSql.Errors.Should().BeEmpty();
        sshOnSql.Job!.Status.Should().Be(JobStatus.PendingApproval);
        sshOnSql.Job.OutsideAllowlist.Should().BeTrue();
        sql.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AToolNotInTheAgentsScope_CannotBeUsed_ByJobsOrTheAdHocTool()
    {
        var sql = new FakeSqlSource();
        await using var h = Harness(sql, ssh: false, sqlScope: [FakeSqlSource.Warehouse]); // only SQL is enabled

        var sshJob = await h.Save(JobHarness.DiskJob() with { Tool = "ssh" });
        var ad = await h.Runner.RunCommandAsync(h.AgentId, "ssh", null, "uptime", default);
        var sshTool = (ToolCompleted)await new SshTool(h.Runner).ExecuteAsync(JsonDocument.Parse("""{"command":"uptime"}""").RootElement,
            new ToolExecutionContext(false, AgentId: h.AgentId), default);

        sshJob.Errors.Should().Contain(e => e.Contains("SSH tool is not enabled for this agent"));
        ad.Error.Should().Contain("SSH tool is not enabled for this agent");
        sshTool.IsError.Should().BeTrue();
        h.Ssh.Commands.Should().BeEmpty();

        (await h.Runner.RunCommandAsync(h.AgentId, "sql", null, "select 1", default)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownTools_AreRefused_WithTheAvailableOnes()
    {
        await using var h = Harness(new FakeSqlSource(), sqlScope: [FakeSqlSource.Warehouse]);

        var result = await h.Save(OrdersJob(tool: "mongo"));

        result.Errors.Should().ContainSingle(e => e.Contains("tool 'mongo' does not exist") && e.Contains("sql, ssh"));
    }

    [Fact]
    public async Task OneAgent_CanMixSshAndSql_EachJobRunningOnItsOwnToolsConnections()
    {
        var sql = new FakeSqlSource();
        await using var h = Harness(sql, sqlScope: [FakeSqlSource.Warehouse, FakeSqlSource.Analytics]);
        await h.Save(JobHarness.DiskJob() with { Tool = "ssh" });
        await h.Save(OrdersJob());

        var result = await h.Runner.RunAsync(h.AgentId, JobSelection.All, null, default);

        result!.Results.Where(r => r.Job == "Disk usage").Select(r => r.Target).Should().Equal("web-1");
        result.Results.Where(r => r.Job == "Orders by status").Select(r => r.Target).Should().BeEquivalentTo("warehouse", "analytics");
        result.MonitoredTargetNames.Should().BeEquivalentTo("web-1", "warehouse", "analytics");
        h.Ssh.Commands.Should().ContainSingle().Which.Should().Be("df -P -B1");
        sql.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Test_RunsOnTheNamedConnectionOfTheJobsTool()
    {
        var sql = new FakeSqlSource();
        await using var h = Harness(sql, sqlScope: [FakeSqlSource.Warehouse, FakeSqlSource.Analytics]);

        var outcome = await h.Runner.TestAsync(h.AgentId, OrdersJob() with { Targets = ["analytics"] }, "analytics", default);

        outcome.Success.Should().BeTrue();
        outcome.Target.Should().Be("analytics");
        outcome.RowCount.Should().Be(2);
        sql.Calls.Should().ContainSingle().Which.Connection.Should().Be(FakeSqlSource.Analytics);
    }

    [Fact]
    public async Task ApprovalRechecksTheJobsOwnTools_Guard()
    {
        var sql = new FakeSqlSource();
        await using var h = new JobHarness(requireApproval: true, configure: s => s.AddSingleton<IDataSource>(sql),
            extraScopes: new Dictionary<string, Guid[]> { ["sql"] = [FakeSqlSource.Warehouse] });
        var job = (await h.Save(OrdersJob())).Job!;

        var approved = await h.Approve(job.Id);

        approved!.Status.Should().Be(JobStatus.Active);
        approved.Tool.Should().Be("sql");
    }

    [Fact]
    public async Task ChangingAJobsTool_SendsItBackForApproval()
    {
        await using var h = new JobHarness(requireApproval: true, configure: s => s.AddSingleton<IDataSource>(new FakeSqlSource()),
            extraScopes: new Dictionary<string, Guid[]> { ["sql"] = [FakeSqlSource.Warehouse] });
        var job = (await h.Save(JobHarness.DiskJob("Same name") with { Tool = "ssh" })).Job!;
        await h.Approve(job.Id);

        var moved = await h.Save(OrdersJob("Same name"));

        moved.Job!.Tool.Should().Be("sql");
        moved.Job.Status.Should().Be(JobStatus.PendingApproval);
    }

    [Fact]
    public async Task AJobWithNoTool_UsesTheAgentsOnlyDataTool_WhateverItIs()
    {
        await using var sshOnly = new JobHarness(requireApproval: false);
        (await sshOnly.Save(JobHarness.DiskJob())).Job!.Tool.Should().Be("ssh");

        await using var sqlOnly = new JobHarness(requireApproval: false, configure: s => s.AddSingleton<IDataSource>(new FakeSqlSource()),
            extraScopes: new Dictionary<string, Guid[]> { ["sql"] = [FakeSqlSource.Warehouse] }, scopeSsh: false);
        (await sqlOnly.Save(OrdersJob(tool: null))).Job!.Tool.Should().Be("sql");
    }

    [Fact]
    public async Task AJobWithNoTool_OnAnAgentWithSeveralTools_MustSayWhich_AndWithNoToolsCannotBeSaved()
    {
        await using var both = new JobHarness(requireApproval: false, configure: s => s.AddSingleton<IDataSource>(new FakeSqlSource()),
            extraScopes: new Dictionary<string, Guid[]> { ["sql"] = [FakeSqlSource.Warehouse] });
        (await both.Save(OrdersJob(tool: null))).Errors.Should().ContainSingle(e => e.Contains("tool is required") && e.Contains("sql, ssh"));

        await using var none = new JobHarness(requireApproval: false, scopeSsh: false);
        (await none.Save(JobHarness.DiskJob())).Errors.Should().Contain(e => e.Contains("no data tool is enabled"));
    }

    [Fact]
    public async Task DataSourcesApi_ListsEachToolWithItsScopeItemsAndConnections()
    {
        var sql = new FakeSqlSource();
        await using var h = Harness(sql, sqlScope: [FakeSqlSource.Warehouse]);

        var result = await h.With(async sp =>
            await new DataSourcesController(sp.GetRequiredService<DataSourceRegistry>()).GetAll(default));

        var list = (Microsoft.AspNetCore.Mvc.OkObjectResult)result.Result!;
        var sources = ((IReadOnlyList<DataSourceDto>)list.Value!).ToList();
        sources.Select(s => s.Tool).Should().Equal("sql", "ssh"); // ordered by display name
        var ssh = sources.Single(s => s.Tool == "ssh");
        ssh.Items.Should().ContainSingle().Which.Should().Match<ScopeItemDto>(i => i.Name == "prod" && i.Kind == "group" && i.Connections.SequenceEqual(new[] { "off", "web-1" }));
        sources.Single(s => s.Tool == "sql").Items.Select(i => (i.Name, i.Kind)).Should().BeEquivalentTo([("warehouse", "connection"), ("analytics", "connection")]);
    }
}

public class AgentScopeConfigTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    [Fact]
    public void ToolScopes_RoundTrip_AndScopeForIsCaseInsensitive()
    {
        var descriptor = new DashboardAlertsAgentTypeDescriptor();
        var (json, error) = descriptor.NormalizeConfig(JsonSerializer.Serialize(new { toolScopes = new Dictionary<string, Guid[]> { ["SSH"] = [A, A], ["sql"] = [B], ["empty"] = [] } }));

        error.Should().BeNull();
        var config = DashboardAlertsConfig.Parse(json);
        config.ToolScopes.Keys.Should().BeEquivalentTo("ssh", "sql"); // lowercased, empties dropped
        config.ScopeFor("SSH").Should().Equal(A); // duplicates dropped
        config.ScopeFor("Sql").Should().Equal(B);
        config.ScopeFor("mongo").Should().BeEmpty();
    }

    [Fact]
    public void AnOlderConfigWithTargetGroupIds_IsReadAsTheSshScope_AndDroppedOnTheNextSave()
    {
        var legacy = JsonSerializer.Serialize(new { targetGroupIds = new[] { A }, collectIntervalMinutes = 3 });

        var parsed = DashboardAlertsConfig.Parse(legacy);
        parsed.ScopeFor("ssh").Should().Equal(A);
        parsed.CollectIntervalMinutes.Should().Be(3);

        var (resaved, _) = new DashboardAlertsAgentTypeDescriptor().NormalizeConfig(legacy);
        resaved.Should().NotContain("targetGroupIds").And.Contain("\"ssh\"");
    }

    [Fact]
    public void ANewScopeWins_OverALegacyListOnTheSameConfig()
    {
        var json = JsonSerializer.Serialize(new { targetGroupIds = new[] { A }, toolScopes = new { ssh = new[] { B } } });

        DashboardAlertsConfig.Parse(json).ScopeFor("ssh").Should().Equal(B);
    }

    [Fact]
    public void ToolsFor_IsTheGenericTools_PlusOneToolPerScopedDataSource_AndNeverHardcodesSsh()
    {
        var d = new DashboardAlertsAgentTypeDescriptor();

        var none = d.ToolsFor("{}");
        none.Should().Contain(["collector_save", "collector_test", "data_query", "dashboard_save", "alert_rule_save"]).And.NotContain("ssh");

        d.ToolsFor(JsonSerializer.Serialize(new { toolScopes = new { ssh = new[] { A } } })).Should().Contain("ssh");
        var onlySql = d.ToolsFor(JsonSerializer.Serialize(new { toolScopes = new Dictionary<string, Guid[]> { ["sql"] = [B], ["ssh"] = [] } }));
        onlySql.Should().Contain("sql").And.NotContain("ssh");
        d.RequiredTools.Should().NotContain("ssh");
    }

    [Fact]
    public void TheGeneralAgentType_GetsNoDataSourceTools() =>
        ((IAgentTypeDescriptor)new GeneralAgentTypeDescriptor()).ToolsFor("{}").Should().BeEmpty();

    [Fact]
    public async Task Registry_ForAgent_ReturnsOnlyTheSourcesWithSomethingSelected()
    {
        await using var h = new JobHarness(configure: s => s.AddSingleton<IDataSource>(new FakeSqlSource()));
        await using var scope = h.Scope();
        var registry = scope.ServiceProvider.GetRequiredService<DataSourceRegistry>();

        registry.ForAgent(DashboardAlertsConfig.Parse("{}")).Should().BeEmpty();
        registry.ForAgent(DashboardAlertsConfig.Parse(JsonSerializer.Serialize(new { toolScopes = new { sql = new[] { B } } })))
            .Select(s => s.ToolName).Should().Equal("sql");
        registry.Find("SSH").Should().NotBeNull();
        registry.Find("nope").Should().BeNull();
    }
}

public class SshDataSourceTests
{
    [Fact]
    public async Task Scope_IsGroups_ResolutionIsEnabledMembersOnly_AndNewMembersJoinAutomatically()
    {
        await using var h = new JobHarness();
        await using var scope = h.Scope();
        var source = scope.ServiceProvider.GetRequiredService<DataSourceRegistry>().Find("ssh")!;
        var groupId = (await source.ListScopeItemsAsync(default)).Single().Id;

        (await source.ResolveAsync([groupId], default)).Select(c => c.Name).Should().Equal("web-1"); // "off" is disabled

        var seedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await TestSeed.SshServerAsync(seedDb, groupId, "web-2", "10.0.0.7", 22, "root", "SSH_K", true, default); // added later
        (await source.ResolveAsync([groupId], default)).Select(c => c.Name).Should().BeEquivalentTo("web-1", "web-2");
        (await source.ResolveAsync([Guid.NewGuid()], default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_RecordsTheServersStatus_PinsTheHostKeyOnlyOnSuccess_AndAJobFailureIsNotAConnectionFailure()
    {
        await using var h = new JobHarness(respond: (_, cmd) => cmd == "uptime" ? JobHarness.Ok("up") : cmd == "fails" ? new SshCommandResult(true, 3, "", "nope", null, "SHA256:fp") : JobHarness.Down("Connection refused"));
        await using var scope = h.Scope();
        var source = scope.ServiceProvider.GetRequiredService<DataSourceRegistry>().Find("ssh")!;
        var servers = scope.ServiceProvider.GetRequiredService<ISshServersRepository>();

        (await source.RunAsync(h.ServerId, "other", TimeSpan.FromSeconds(1), default)).Connected.Should().BeFalse();
        var down = (await servers.GetServerAsync(h.ServerId, default))!;
        down.LastError.Should().Be("Connection refused");
        down.HostKeyFingerprint.Should().BeNull();

        (await source.RunAsync(h.ServerId, "fails", TimeSpan.FromSeconds(1), default)).ExitCode.Should().Be(3);
        var reached = (await servers.GetServerAsync(h.ServerId, default))!;
        reached.LastError.Should().BeNull(); // the command failed, but the server was reached
        reached.HostKeyFingerprint.Should().Be("SHA256:fp");

        (await source.RunAsync(Guid.NewGuid(), "uptime", TimeSpan.FromSeconds(1), default)).Error.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task Check_UsesTheLiveAllowlist()
    {
        await using var h = new JobHarness();
        await using var scope = h.Scope();
        var source = scope.ServiceProvider.GetRequiredService<DataSourceRegistry>().Find("ssh")!;

        source.Check("df -h").Allowed.Should().BeTrue();
        h.Cache.SetTool("ssh", new Dictionary<string, string?> { ["AllowedCommands"] = "uptime" });
        source.Check("df -h").Allowed.Should().BeFalse();
        source.RequestNoun.Should().Be("command");
    }
}
