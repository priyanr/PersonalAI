using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents;
using PersonalAI.Core.Services.Monitoring;
using PersonalAI.Core.Services.Monitoring.Alerts;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Monitoring.Dashboards;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Ssh;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Services;

/// <summary>Regression tests for the code-review findings: each fails against the behavior it replaced.</summary>
public class SshSecretRestrictionTests
{
    private sealed class NeverCalledSsh : ISshRunner
    {
        public Task<SshCommandResult> RunAsync(SshServer server, string command, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("must not connect");
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Theory]
    [InlineData("SSH_HOMELAB_KEY", true)]
    [InlineData("ssh_lowercase", true)]
    [InlineData("SSH_", false)]
    [InlineData("OPENAI_API_KEY", false)]
    [InlineData("MY_SSH_KEY", false)]
    [InlineData(null, false)]
    public void IsAllowed_OnlySshPrefixedSecrets(string? name, bool expected) =>
        SshSecrets.IsAllowed(name).Should().Be(expected);

    [Fact]
    public async Task Controller_RejectsAServerThatNamesAnUnrelatedSecret_EvenThoughItExists()
    {
        await using var db = NewDb();
        var repo = new SshServersRepository(db);
        var group = await repo.CreateGroupAsync("g", null, default);
        await new SecretsRepository(db).CreateAsync("OPENAI_API_KEY", "sk-secret", default);
        var controller = new SshServersController(repo, new SecretsRepository(db), new SshConnectionService(repo, new NeverCalledSsh()));

        var response = await controller.CreateServer(
            new SaveSshServerRequest(group.Id, "evil", "attacker.example.com", 22, "root", "OPENAI_API_KEY"), default);

        var bad = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("can't be used for SSH").And.Contain("SSH_");
        (await repo.GetServersAsync(null, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Runner_NeverSendsANonSshSecret_EvenForAnAlreadyStoredServer()
    {
        await using var db = NewDb();
        await new SecretsRepository(db).CreateAsync("OPENAI_API_KEY", "sk-secret", default);
        var server = new SshServer { Name = "t", Address = "127.0.0.1", Port = 1, Username = "root", SecretRef = "OPENAI_API_KEY" };

        var result = await new SshNetRunner(new SecretsRepository(db)).RunAsync(server, "uname", TimeSpan.FromSeconds(1), default);

        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("can't be used for SSH");
    }
}

public class DashboardStalenessAndSeriesTests
{
    private static (ServiceProvider P, Guid Agent) Build()
    {
        var services = new ServiceCollection();
        var name = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        return (services.BuildServiceProvider(), Guid.NewGuid());
    }

    private static NewDataPoint P(string dataset, string entity, DateTime ts, object values, DatasetKind kind = DatasetKind.Timeseries) =>
        new(dataset, kind, entity, ts, JsonSerializer.Serialize(values));

    private static async Task<Dictionary<string, object>> BuildAsync(ServiceProvider p, Guid agent, string widgetsJson)
    {
        await using var scope = p.CreateAsyncScope();
        var (spec, error) = DashboardSpecJson.Parse($$"""{"title":"T","widgets":{{widgetsJson}}}""");
        spec.Should().NotBeNull(error);
        return await new DashboardDataBuilder(scope.ServiceProvider.GetRequiredService<IDatasetRepository>()).BuildAsync(agent, spec!, default);
    }

    [Fact]
    public async Task LatestBasedWidgets_IgnoreRowsThatStoppedReporting()
    {
        var (p, agent) = Build();
        var now = DateTime.UtcNow;
        await using (var scope = p.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agent,
            [
                P("container", "t|web", now.AddMinutes(-1), new { target = "web-1", name = "web", state = "running", running = true }, DatasetKind.Snapshot),
                P("container", "t|removed", now.AddHours(-3), new { target = "web-1", name = "removed", state = "running", running = true }, DatasetKind.Snapshot)
            ], default);

        var data = await BuildAsync(p, agent, """
            [{"id":"t","type":"table","query":{"dataset":"container"},"options":{"columns":["name","state"]}},
             {"id":"g","type":"statusGrid","query":{"dataset":"container","field":"running"}},
             {"id":"b","type":"bar","query":{"dataset":"container","field":"running"}},
             {"id":"n","type":"stat","query":{"dataset":"container","field":"running","agg":"count"}}]
            """);

        ((TableData)data["t"]).Rows.Should().ContainSingle().Which[0].Should().Be("web");
        ((GridData)data["g"]).Items.Should().ContainSingle().Which.Label.Should().Contain("web-1 · web");
        ((BarData)data["b"]).Items.Should().ContainSingle();
        ((StatData)data["n"]).Value.Should().Be(1);
    }

    [Fact]
    public async Task Stat_WhenEverythingIsStale_SaysNoData()
    {
        var (p, agent) = Build();
        await using (var scope = p.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agent,
                [P("system", "a", DateTime.UtcNow.AddDays(-2), new { target = "web-1", cpuPercent = 99.0 })], default);

        var data = await BuildAsync(p, agent, """[{"id":"s","type":"stat","query":{"dataset":"system","field":"cpuPercent"}}]""");

        ((StatData)data["s"]).Message.Should().Be("No data");
    }

    [Fact]
    public async Task Timeseries_StillDrawsHistoryOfAnEntityThatStoppedReportingWithinTheWindow()
    {
        var (p, agent) = Build();
        var now = DateTime.UtcNow;
        await using (var scope = p.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agent,
                [.. Enumerable.Range(0, 6).Select(i => P("system", "a", now.AddMinutes(-240 + i * 5), new { target = "gone", cpuPercent = 30.0 + i }))], default);

        var data = await BuildAsync(p, agent, """[{"id":"c","type":"timeseries","query":{"dataset":"system","field":"cpuPercent","windowMinutes":360}}]""");

        ((TimeseriesData)data["c"]).Series.Should().ContainSingle().Which.Label.Should().Be("gone");
    }

    [Fact]
    public async Task Timeseries_ADownServerWhoseNewestPointHasNoValue_KeepsItsHistory_AndRanksLast()
    {
        var (p, agent) = Build();
        var now = DateTime.UtcNow;
        await using (var scope = p.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agent,
            [
                P("system", "up", now.AddMinutes(-10), new { target = "up", cpuPercent = 10.0 }),
                P("system", "up", now, new { target = "up", cpuPercent = 12.0 }),
                P("system", "down", now.AddMinutes(-10), new { target = "down", cpuPercent = 50.0 }),
                P("system", "down", now, new { target = "down", reachable = false })
            ], default);

        var data = await BuildAsync(p, agent, """[{"id":"c","type":"timeseries","query":{"dataset":"system","field":"cpuPercent","windowMinutes":60,"bucketMinutes":60}}]""");

        ((TimeseriesData)data["c"]).Series.Select(s => s.Label).Should().BeEquivalentTo("up", "down");
    }

    [Fact]
    public async Task Timeseries_LongWindow_KeepsTheWholeRangeForEachEntity_NotJustTheNewestSlice()
    {
        var (p, agent) = Build();
        var now = DateTime.UtcNow;
        // 7 days of 1-minute samples for 2 servers = 20,160 points. One global fetch capped at 20,000 used to drop the
        // oldest hours of the window silently.
        await using (var scope = p.CreateAsyncScope())
        {
            var points = new List<NewDataPoint>();
            foreach (var server in new[] { "a", "b" })
                for (var i = 0; i < 7 * 24 * 60; i++)
                    points.Add(P("system", server, now.AddMinutes(-(7 * 24 * 60) + i + 1), new { target = server, cpuPercent = 10.0 + (i % 7) }));
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agent, points, default);
        }

        var data = await BuildAsync(p, agent, """[{"id":"c","type":"timeseries","query":{"dataset":"system","field":"cpuPercent","windowMinutes":10080,"bucketMinutes":180}}]""");

        foreach (var series in ((TimeseriesData)data["c"]).Series)
        {
            var oldest = DateTimeOffset.FromUnixTimeMilliseconds((long)series.Points[0][0]).UtcDateTime;
            oldest.Should().BeBefore(now.AddDays(-6.8), $"series {series.Label} must reach back to the start of the 7-day window");
        }
    }

    [Fact]
    public async Task Timeseries_PicksTheWorstEightOfManyEntities()
    {
        var (p, agent) = Build();
        var now = DateTime.UtcNow;
        await using (var scope = p.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agent,
                [.. Enumerable.Range(1, 12).Select(i => P("system", $"s{i}", now, new { target = $"s{i:00}", cpuPercent = (double)i }))], default);

        var data = await BuildAsync(p, agent, """[{"id":"c","type":"timeseries","query":{"dataset":"system","field":"cpuPercent","windowMinutes":30}}]""");

        var ts = (TimeseriesData)data["c"];
        ts.Series.Should().HaveCount(8);
        ts.HiddenSeries.Should().Be(4);
        ts.Series.Select(s => s.Label).Should().BeEquivalentTo("s05", "s06", "s07", "s08", "s09", "s10", "s11", "s12");
    }
}

public class AlertLifecycleFixTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task EntityThatDisappears_WhileTheRestOfTheDatasetUpdates_IsResolvedAsNoLongerReporting()
    {
        await using var f = new AlertEvaluatorTests.Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Sample("web-2", T0, 10);
        await f.Eval(T0);
        (await f.Events()).Should().ContainSingle(e => e.EntityLabel == "web-1" && e.State == AlertState.Firing);

        // two hours later web-2 is still reporting, web-1 never did again (a removed container, a deleted server)
        await f.Sample("web-2", T0.AddHours(2), 10);
        var result = await f.Eval(T0.AddHours(2));

        result.Resolved.Should().Be(1);
        var e = (await f.Events()).Single();
        e.State.Should().Be(AlertState.Resolved);
        e.Message.Should().Contain("no longer reporting");
    }

    [Fact]
    public async Task WholeDatasetGoingQuiet_IsACollectorOutage_SoAlertsStayFiring()
    {
        await using var f = new AlertEvaluatorTests.Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Sample("web-2", T0, 96);
        await f.Eval(T0);

        var result = await f.Eval(T0.AddHours(5)); // nothing new for anyone

        result.Resolved.Should().Be(0);
        (await f.Events()).Should().HaveCount(2).And.OnlyContain(e => e.State == AlertState.Firing);
    }

    [Fact]
    public async Task ServerGoingDown_ClosesItsCpuAlert_BecauseTheFieldIsNoLongerReported()
    {
        await using var f = new AlertEvaluatorTests.Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);
        await f.S.GetRequiredService<IDatasetRepository>().AddPointsAsync(f.AgentId,
            [new("system", DatasetKind.Timeseries, "web-1", T0.AddMinutes(5), JsonSerializer.Serialize(new { target = "web-1", reachable = false }))], default);

        (await f.Eval(T0.AddMinutes(5))).Resolved.Should().Be(1);

        (await f.Events()).Single().Message.Should().Contain("cpuPercent is no longer reported");
    }

    [Fact]
    public async Task RuleNarrowedBehindTheEvaluatorsBack_ResolvesAlertsForEntitiesItNoLongerMatches()
    {
        await using var f = new AlertEvaluatorTests.Fixture();
        var rule = await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Sample("web-2", T0, 96);
        await f.Eval(T0);

        var tracked = (await f.Alerts.GetRuleAsync(f.AgentId, rule.Id, default))!;
        tracked.Target = "web-2";
        await f.Alerts.SaveRuleAsync(tracked, default);
        await f.Sample("web-1", T0.AddMinutes(5), 95);
        await f.Sample("web-2", T0.AddMinutes(5), 96);
        await f.Eval(T0.AddMinutes(5));

        var events = await f.Events();
        events.Single(e => e.EntityLabel == "web-1").Message.Should().Contain("no longer matches the rule");
        events.Single(e => e.EntityLabel == "web-2").State.Should().Be(AlertState.Firing);
    }

    [Fact]
    public async Task EditingWhatARuleWatches_ResolvesItsOpenAlertsImmediately_ButAThresholdChangeDoesNot()
    {
        await using var f = new AlertEvaluatorTests.Fixture();
        var rule = await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Sample("web-2", T0, 96);
        await f.Eval(T0);
        var svc = f.S.GetRequiredService<AlertRuleService>();
        AlertRuleInput Input(double threshold, string? target) => new(rule.Name, null, "system", "cpuPercent", "gte", threshold, 0, "critical", target, null, true);

        (await svc.UpdateAsync(f.AgentId, rule.Id, Input(80, null), default))!.IsValid.Should().BeTrue();
        (await f.Events()).Should().HaveCount(2).And.OnlyContain(e => e.State == AlertState.Firing);

        (await svc.UpdateAsync(f.AgentId, rule.Id, Input(80, "web-1"), default))!.IsValid.Should().BeTrue();

        (await f.Events()).Should().HaveCount(2).And.OnlyContain(e => e.State == AlertState.Resolved && e.Message.Contains("rule changed"));
    }
}

public class DataQueryScopingTests
{
    private sealed class IdleSsh : ISshRunner
    {
        public Task<SshCommandResult> RunAsync(SshServer server, string command, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("not expected");
    }

    [Fact]
    public async Task Target_IsResolvedAmongTheCallingAgentsOwnServers_NotAcrossTheFleet()
    {
        var services = new ServiceCollection();
        var db = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddScoped<IAgentsRepository, AgentsRepository>();
        services.AddScoped<ISshServersRepository, SshServersRepository>();
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        services.AddSingleton<ISshRunner>(new IdleSsh());
        services.AddSingleton(new PersonalAI.Core.Services.Tools.ToolConfigCache());
        services.AddSingleton<SshGuardProvider>();
        services.AddScoped<IDataSource, SshDataSource>();
        services.AddScoped<DataSourceRegistry>();
        var provider = services.BuildServiceProvider();

        Guid agentId, ownServerId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var servers = scope.ServiceProvider.GetRequiredService<ISshServersRepository>();
            // The other team's group is created first and also has a "web-1": an arbitrary global name lookup finds it.
            var other = await servers.CreateGroupAsync("other-team", null, default);
            await servers.CreateServerAsync(other.Id, "web-1", "10.1.0.1", 22, "root", "SSH_K", true, default);
            await servers.CreateServerAsync(other.Id, "secret-box", "10.1.0.2", 22, "root", "SSH_K", true, default);
            var mine = await servers.CreateGroupAsync("mine", null, default);
            ownServerId = (await servers.CreateServerAsync(mine.Id, "web-1", "10.0.0.1", 22, "root", "SSH_K", true, default))!.Id;

            var agent = await scope.ServiceProvider.GetRequiredService<IAgentsRepository>().CreateAsync(
                "mon", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Manual, null, null, null, true, default,
                null, AgentType.DashboardAlerts, JsonSerializer.Serialize(new { toolScopes = new { ssh = new[] { mine.Id } } }));
            agentId = agent.Id;
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agentId,
                [new("system", DatasetKind.Timeseries, ownServerId.ToString(), DateTime.UtcNow, JsonSerializer.Serialize(new { target = "web-1", cpuPercent = 42.0 }))], default);
        }

        var tool = new DataQueryTool(provider.GetRequiredService<IServiceScopeFactory>());
        var ctx = new ToolExecutionContext(false, AgentId: agentId);

        var found = (ToolCompleted)await tool.ExecuteAsync(JsonDocument.Parse("""{"dataset":"system","target":"web-1","latest":true}""").RootElement, ctx, default);
        found.IsError.Should().BeFalse();
        found.Content.Should().Contain("\"cpuPercent\":42");

        var missing = (ToolCompleted)await tool.ExecuteAsync(JsonDocument.Parse("""{"dataset":"system","target":"secret-box"}""").RootElement, ctx, default);
        missing.IsError.Should().BeTrue();
        // the other team's "secret-box" exists, but this agent doesn't monitor it, and only its own servers are listed
        missing.Content.Should().Be("No connection named 'secret-box'. This agent monitors: web-1.");
    }
}

/// <summary>Real SQLite, where the state and date filters must translate to SQL.</summary>
public class AlertQueriesOnSqliteTests
{
    [Fact]
    public async Task EventFilters_AndSummary_RunInTheDatabase()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var _c = conn;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var agent = await new AgentsRepository(db).CreateAsync("a", null, "p", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, default, null, AgentType.DashboardAlerts, "{}");
        var repo = new AlertsRepository(db);
        var critical = await repo.AddRuleAsync(new AlertRule { AgentId = agent.Id, Name = "crit", Dataset = "d", Field = "f", Operator = "gte", Severity = "critical" }, default);
        var warning = await repo.AddRuleAsync(new AlertRule { AgentId = agent.Id, Name = "warn", Dataset = "d", Field = "f", Operator = "gte", Severity = "warning" }, default);

        var now = DateTime.UtcNow;
        AlertEvent E(AlertRule r, string key, AlertState state, DateTime at, DateTime? resolved = null, bool ack = false) => new()
        {
            RuleId = r.Id, AgentId = agent.Id, EntityKey = key, EntityLabel = key, State = state, Message = key,
            FirstSeen = at, FiredAt = at, LastSeen = at, ResolvedAt = resolved, Acknowledged = ack
        };
        await repo.AddEventAsync(E(critical, "c1", AlertState.Firing, now.AddMinutes(-5)), default);
        await repo.AddEventAsync(E(critical, "c2", AlertState.Firing, now.AddMinutes(-4), ack: true), default);
        await repo.AddEventAsync(E(warning, "w1", AlertState.Firing, now.AddMinutes(-3)), default);
        await repo.AddEventAsync(E(warning, "old", AlertState.Resolved, now.AddDays(-9), now.AddDays(-9)), default);
        await repo.AddEventAsync(E(warning, "new", AlertState.Resolved, now.AddHours(-2), now.AddHours(-1)), default);
        await repo.AddEventAsync(E(warning, "p", AlertState.Pending, now), default);

        (await repo.GetEventsAsync(null, true, null, default)).Select(e => e.EntityKey).Should().BeEquivalentTo("c1", "c2", "w1");
        (await repo.GetEventsAsync(null, false, now.AddDays(-7), default)).Select(e => e.EntityKey).Should().Equal("new");
        (await repo.GetEventsAsync(null, true, now.AddDays(-7), default)).Select(e => e.EntityKey).Should().BeEquivalentTo("c1", "c2", "w1", "new");
        (await repo.GetEventsAsync(null, false, null, default)).Should().BeEmpty();
        (await repo.GetEventsAsync(Guid.NewGuid(), true, null, default)).Should().BeEmpty();

        (await repo.GetSummaryAsync(default)).Should().Be(new AlertSummary(Firing: 3, Critical: 2, Warning: 1, Unacknowledged: 2, UnacknowledgedCritical: 1));
    }
}
