using PersonalAI.Core.Models;
using PersonalAI.Core.Services.Monitoring.Collectors;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents;
using PersonalAI.Core.Services.Monitoring;
using PersonalAI.Core.Services.Monitoring.Alerts;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Ssh;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Services;

public class AlertEvaluatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);

    internal sealed class Fixture : IAsyncDisposable
    {
        public ServiceProvider Provider { get; }
        public Guid AgentId { get; } = Guid.NewGuid();
        private readonly AsyncServiceScope _scope;

        public Fixture()
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.AddScoped<IDatasetRepository, DatasetRepository>();
            services.AddScoped<IAlertsRepository, AlertsRepository>();
            services.AddScoped<AlertRuleService>();
            services.AddScoped<AlertsService>();
            services.AddScoped<DatasetService>();
            services.AddScoped<IAgentsRepository, AgentsRepository>();
            services.AddScoped<ICollectionJobsRepository, CollectionJobsRepository>();
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AlertEvaluator>>(NullLogger<AlertEvaluator>.Instance);
            services.AddScoped<AlertEvaluator>();
            Provider = services.BuildServiceProvider();
            _scope = Provider.CreateAsyncScope();
        }

        public IServiceProvider S => _scope.ServiceProvider;
        public IAlertsRepository Alerts => S.GetRequiredService<IAlertsRepository>();
        public AlertsService AlertsSvc => S.GetRequiredService<AlertsService>();
        public AlertEvaluator Evaluator => S.GetRequiredService<AlertEvaluator>();

        public Task Sample(string target, DateTime ts, double cpu, bool reachable = true) =>
            S.GetRequiredService<DatasetService>().AddPointsAsync(AgentId,
                [new("system", DatasetKind.Timeseries, target, ts, JsonSerializer.Serialize(new { target, reachable, cpuPercent = cpu }))], default);

        public async Task<AlertRule> Rule(string name = "High CPU", string field = "cpuPercent", string op = "gte", double threshold = 90, int forMinutes = 0, string? target = null)
        {
        // A rule may only name a dataset that exists (or a job declares), as in production.
            await S.GetRequiredService<DatasetService>().EnsureDatasetsAsync(AgentId, [new("system", DatasetKind.Timeseries, null)], default);
            var r = await S.GetRequiredService<AlertRuleService>().CreateAsync(AgentId,
                new(name, null, "system", field, op, threshold, forMinutes, "critical", target, null, true), AlertSources.User, default);
            r.Errors.Should().BeEmpty();
            return r.Rule!;
        }

        public Task<AlertEvaluationResult> Eval(DateTime now) => Evaluator.EvaluateAsync(AgentId, now, Stale, default);

        public async Task<List<AlertEvent>> Events() =>
            [.. await Alerts.GetEventsAsync(AgentId, true, T0.AddYears(-1), default)];

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }

    [Fact]
    public async Task Immediate_Rule_FiresOnFirstViolation_ThenResolvesWhenClear()
    {
        await using var f = new Fixture();
        await f.Sample("web-1", T0, 95);
        await f.Rule();

        (await f.Eval(T0)).Should().Be(new AlertEvaluationResult(1, 0));
        var fired = (await f.Events()).Single();
        fired.State.Should().Be(AlertState.Firing);
        fired.EntityLabel.Should().Be("web-1");
        fired.Message.Should().Contain("cpuPercent is 95").And.Contain("≥ 90");

        await f.Sample("web-1", T0.AddMinutes(5), 40);
        (await f.Eval(T0.AddMinutes(5))).Should().Be(new AlertEvaluationResult(0, 1));
        var resolved = (await f.Events()).Single();
        resolved.State.Should().Be(AlertState.Resolved);
        resolved.ResolvedAt.Should().Be(T0.AddMinutes(5));
        resolved.Message.Should().Contain("back to normal");
    }

    [Fact]
    public async Task StillViolating_UpdatesTheSameEvent_NotANewOne()
    {
        await using var f = new Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 91);
        await f.Eval(T0);
        await f.Sample("web-1", T0.AddMinutes(5), 97);

        (await f.Eval(T0.AddMinutes(5))).Fired.Should().Be(0);

        var e = (await f.Events()).Single();
        e.Value.Should().Be(97);
        e.LastSeen.Should().Be(T0.AddMinutes(5));
        e.FiredAt.Should().Be(T0);
    }

    [Fact]
    public async Task ForMinutes_StaysPending_UntilHeldLongEnough()
    {
        await using var f = new Fixture();
        await f.Rule(forMinutes: 10);

        await f.Sample("web-1", T0, 95);
        (await f.Eval(T0)).Fired.Should().Be(0);
        await f.Sample("web-1", T0.AddMinutes(5), 96);
        (await f.Eval(T0.AddMinutes(5))).Fired.Should().Be(0);
        (await f.Events()).Should().BeEmpty(); // pending is never shown

        await f.Sample("web-1", T0.AddMinutes(10), 97);
        (await f.Eval(T0.AddMinutes(10))).Fired.Should().Be(1);

        var e = (await f.Events()).Single();
        e.FirstSeen.Should().Be(T0);
        e.FiredAt.Should().Be(T0.AddMinutes(10));
    }

    [Fact]
    public async Task ForMinutes_ABlipThatClears_NeverFires()
    {
        await using var f = new Fixture();
        await f.Rule(forMinutes: 10);
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);
        await f.Sample("web-1", T0.AddMinutes(5), 20);

        (await f.Eval(T0.AddMinutes(5))).Should().Be(new AlertEvaluationResult(0, 0));

        (await f.Events()).Should().BeEmpty();
        await f.Sample("web-1", T0.AddMinutes(10), 95); // a later spike starts the clock again
        (await f.Eval(T0.AddMinutes(10))).Fired.Should().Be(0);
    }

    [Fact]
    public async Task BooleanField_EqZero_AlertsOnADownServer_WithReadableMessage()
    {
        await using var f = new Fixture();
        await f.Rule("Server down", "reachable", "eq", 0);
        await f.Sample("web-1", T0, 10, reachable: true);
        await f.Sample("nas-1", T0, 0, reachable: false);

        (await f.Eval(T0)).Fired.Should().Be(1);

        (await f.Events()).Single().Message.Should().Be("nas-1: reachable is false");
    }

    [Fact]
    public async Task EachEntityHasItsOwnAlert_AndTargetFilterNarrowsRule()
    {
        await using var f = new Fixture();
        await f.Rule("all");
        await f.Rule("only web-2", target: "web-2");
        await f.Sample("web-1", T0, 95);
        await f.Sample("web-2", T0, 99);

        (await f.Eval(T0)).Fired.Should().Be(3); // 2 for "all" + 1 for "only web-2"
    }

    [Fact]
    public async Task StaleSamples_NeitherFireNorResolve()
    {
        await using var f = new Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);

        // collection stopped for 2 hours: the newest sample is old, so nothing changes
        (await f.Eval(T0.AddHours(2))).Should().Be(new AlertEvaluationResult(0, 0));
        (await f.Events()).Single().State.Should().Be(AlertState.Firing);

        await using var g = new Fixture();
        await g.Rule();
        await g.Sample("web-1", T0, 99);
        (await g.Eval(T0.AddHours(2))).Fired.Should().Be(0); // old data doesn't open an alert either
    }

    [Fact]
    public async Task DisabledRule_IsSkipped_AndDisablingResolvesItsOpenAlerts()
    {
        await using var f = new Fixture();
        var rule = await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);

        var result = await f.S.GetRequiredService<AlertRuleService>().UpdateAsync(f.AgentId, rule.Id,
            new(rule.Name, null, "system", "cpuPercent", "gte", 90, 0, "critical", null, null, false), default);

        result!.IsValid.Should().BeTrue();
        var e = (await f.Events()).Single();
        e.State.Should().Be(AlertState.Resolved);
        e.Message.Should().Contain("rule disabled");
        await f.Sample("web-1", T0.AddMinutes(5), 99);
        (await f.Eval(T0.AddMinutes(5))).Fired.Should().Be(0);
    }

    [Fact]
    public async Task Acknowledge_PersistsUntilResolved_AndPendingCannotBeAcknowledged()
    {
        await using var f = new Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);
        var id = (await f.Events()).Single().Id;

        await f.AlertsSvc.Acknowledge(id, default);
        await f.Sample("web-1", T0.AddMinutes(5), 99);
        await f.Eval(T0.AddMinutes(5));

        (await f.Events()).Single().Acknowledged.Should().BeTrue();
        await FluentActions.Awaiting(() => f.AlertsSvc.Acknowledge(Guid.NewGuid(), default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Unacknowledge_TakesTheAcknowledgementBack()
    {
        await using var f = new Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);
        var id = (await f.Events()).Single().Id;
        await f.AlertsSvc.Acknowledge(id, default);

        await f.AlertsSvc.Unacknowledge(id, default);

        var e = (await f.Events()).Single();
        e.Acknowledged.Should().BeFalse();
        e.AcknowledgedAt.Should().BeNull();
        (await f.Alerts.GetSummaryAsync(default)).Unacknowledged.Should().Be(1);
        await FluentActions.Awaiting(() => f.AlertsSvc.Unacknowledge(Guid.NewGuid(), default)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task AcknowledgeAll_OnlyTouchesFiringUnacknowledgedAlerts_AndTheSummaryCountsWhatStillNeedsAttention()
    {
        await using var f = new Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Sample("web-2", T0, 96);
        await f.Eval(T0);
        var first = (await f.Events()).First();
        await f.AlertsSvc.Acknowledge(first.Id, default);
        var before = await f.Alerts.GetSummaryAsync(default);
        before.Should().Be(new AlertSummary(Firing: 2, Critical: 2, Warning: 0, Unacknowledged: 1, UnacknowledgedCritical: 1));

        (await f.AlertsSvc.AcknowledgeAll(null, default)).Acknowledged.Should().Be(1);
        (await f.AlertsSvc.AcknowledgeAll(null, default)).Acknowledged.Should().Be(0);

        var after = await f.Alerts.GetSummaryAsync(default);
        after.Firing.Should().Be(2);
        after.Unacknowledged.Should().Be(0);
        after.UnacknowledgedCritical.Should().Be(0);
        (await f.AlertsSvc.AcknowledgeAll(Guid.NewGuid(), default)).Acknowledged.Should().Be(0); // another agent: nothing to do
    }

    [Fact]
    public async Task AnAcknowledgedAlert_ResolvesOnItsOwn_AndComingBackIsANewUnacknowledgedAlert()
    {
        await using var f = new Fixture();
        await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);
        await f.AlertsSvc.Acknowledge((await f.Events()).Single().Id, default);

        await f.Sample("web-1", T0.AddMinutes(5), 40);
        await f.Eval(T0.AddMinutes(5));
        (await f.Alerts.GetEventsAsync(null, true, null, default)).Should().BeEmpty(); // resolved without anyone doing anything

        await f.Sample("web-1", T0.AddMinutes(10), 97);
        await f.Eval(T0.AddMinutes(10));

        var firing = (await f.Alerts.GetEventsAsync(null, true, null, default)).Single();
        firing.Acknowledged.Should().BeFalse();
        (await f.Alerts.GetSummaryAsync(default)).Unacknowledged.Should().Be(1);
    }

    [Fact]
    public async Task DeletingARule_RemovesItsAlerts()
    {
        await using var f = new Fixture();
        var rule = await f.Rule();
        await f.Sample("web-1", T0, 95);
        await f.Eval(T0);

        await f.S.GetRequiredService<AlertRuleService>().DeleteRuleAsync(f.AgentId, rule.Id, default);

        (await f.Events()).Should().BeEmpty();
        (await f.Alerts.GetRulesAsync(f.AgentId, default)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("gt", 90, 90, false)]
    [InlineData("gte", 90, 90, true)]
    [InlineData("lt", 10, 10, false)]
    [InlineData("lte", 10, 10, true)]
    [InlineData("eq", 5, 5, true)]
    [InlineData("ne", 5, 5, false)]
    public void Compare_Operators(string op, double value, double threshold, bool expected) =>
        AlertOperators.Compare(value, op, threshold).Should().Be(expected);
}

public class AlertRuleServiceTests
{
    private static async Task<(ServiceProvider P, Guid Agent)> BuildAsync()
    {
        var services = new ServiceCollection();
        var db = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        services.AddScoped<IAlertsRepository, AlertsRepository>();
        services.AddScoped<AlertRuleService>();
        services.AddScoped<DatasetService>();
        services.AddScoped<IAgentsRepository, AgentsRepository>();
        services.AddScoped<ICollectionJobsRepository, CollectionJobsRepository>();
        var provider = services.BuildServiceProvider();
        var agent = Guid.NewGuid();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatasetService>().AddPointsAsync(agent,
        [
            new("system", DatasetKind.Timeseries, "a", DateTime.UtcNow, JsonSerializer.Serialize(new { target = "web-1", reachable = true, cpuPercent = 10.0, note = "text" })),
            new("disk", DatasetKind.Timeseries, "a|/", DateTime.UtcNow, JsonSerializer.Serialize(new { target = "web-1", mount = "/", usedPercent = 50.0 }))
        ], default);
        return (provider, agent);
    }

    private static AlertRuleInput Input(string name = "r", string dataset = "system", string field = "cpuPercent", string op = "gte", double? threshold = 90,
        int? forMinutes = null, string? severity = "warning", string? target = null, Dictionary<string, string>? filter = null) =>
        new(name, null, dataset, field, op, threshold, forMinutes, severity, target, filter, null);

    [Fact]
    public async Task Create_ValidatesAgainstRealData_WithAlternativesInTheMessage()
    {
        var (p, agent) = await BuildAsync();
        await using var scope = p.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<AlertRuleService>();

        var bad = await svc.CreateAsync(agent, new("", null, "nope", "x", "between", null, 5000, "urgent", null, null, null), AlertSources.User, default);
        bad.Errors.Should().Contain(e => e.Contains("name is required"))
            .And.Contain(e => e.Contains("operator must be one of"))
            .And.Contain(e => e.Contains("threshold is required"))
            .And.Contain(e => e.Contains("forMinutes"))
            .And.Contain(e => e.Contains("severity"));

        (await svc.CreateAsync(agent, Input(dataset: "nope"), AlertSources.User, default)).Errors
            .Should().ContainSingle(e => e.Contains("no dataset 'nope'") && e.Contains("system") && e.Contains("disk"));

        var wrongField = await svc.CreateAsync(agent, Input(field: "cpu"), AlertSources.User, default);
        wrongField.Errors.Should().ContainSingle(e => e.Contains("'cpu' is not a numeric") && e.Contains("cpuPercent") && e.Contains("reachable") && !e.Contains("note"));

        (await svc.CreateAsync(agent, Input(field: "note"), AlertSources.User, default)).IsValid.Should().BeFalse(); // text field

        var extras = await svc.CreateAsync(agent, Input(target: "ghost", filter: new() { ["drive"] = "/" }), AlertSources.User, default);
        extras.Errors.Should().Contain(e => e.Contains("filter key 'drive'")).And.Contain(e => e.Contains("no connection named 'ghost'") && e.Contains("web-1"));

        (await svc.CreateAsync(agent, Input(field: "reachable", op: "eq", threshold: 0), AlertSources.User, default)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateNames_AreRejected_CaseInsensitively()
    {
        var (p, agent) = await BuildAsync();
        await using var scope = p.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<AlertRuleService>();
        (await svc.CreateAsync(agent, Input("High CPU"), AlertSources.User, default)).IsValid.Should().BeTrue();

        var dup = await svc.CreateAsync(agent, Input("high cpu"), AlertSources.User, default);

        dup.Errors.Should().ContainSingle(e => e.Contains("already exists"));
    }

    [Fact]
    public async Task Upsert_UpdatesOwnRules_ButRefusesToTouchTheUsers()
    {
        var (p, agent) = await BuildAsync();
        await using var scope = p.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<AlertRuleService>();
        await svc.CreateAsync(agent, Input("mine", threshold: 80), AlertSources.Llm, default);
        await svc.CreateAsync(agent, Input("theirs"), AlertSources.User, default);

        var updated = await svc.UpsertByNameAsync(agent, Input("MINE", threshold: 95), AlertSources.Llm, default);
        var refused = await svc.UpsertByNameAsync(agent, Input("theirs", threshold: 1), AlertSources.Llm, default);

        updated.IsValid.Should().BeTrue();
        updated.Created.Should().BeFalse();
        updated.Rule!.Threshold.Should().Be(95);
        refused.IsValid.Should().BeFalse();
        refused.Errors.Single().Should().Contain("created by the user");
        (await svc.DeleteByNameAsync(agent, "theirs", AlertSources.Llm, default)).Should().Contain("created by the user");
        (await svc.DeleteByNameAsync(agent, "mine", AlertSources.Llm, default)).Should().BeNull();
        (await svc.DeleteByNameAsync(agent, "mine", AlertSources.Llm, default)).Should().Contain("No rule named");
    }

    [Fact]
    public async Task RuleCount_IsCapped()
    {
        var (p, agent) = await BuildAsync();
        await using var scope = p.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<AlertRuleService>();
        for (var i = 0; i < AlertRuleService.MaxRules; i++)
            (await svc.CreateAsync(agent, Input($"r{i}"), AlertSources.User, default)).IsValid.Should().BeTrue();

        (await svc.CreateAsync(agent, Input("one-too-many"), AlertSources.User, default)).Errors
            .Should().ContainSingle(e => e.Contains("at most 50"));
    }

    [Fact]
    public async Task Tools_SaveListDelete_RoundTrip()
    {
        var (p, agent) = await BuildAsync();
        var scopes = p.GetRequiredService<IServiceScopeFactory>();
        var ctx = new ToolExecutionContext(false, AgentId: agent);
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

        var bad = (ToolCompleted)await new AlertRuleSaveTool(scopes).ExecuteAsync(
            J("""{"name":"x","dataset":"system","field":"cpu","operator":"gte","threshold":90,"severity":"critical"}"""), ctx, default);
        bad.IsError.Should().BeTrue();
        bad.Content.Should().Contain("Not saved").And.Contain("cpuPercent");

        var ok = (ToolCompleted)await new AlertRuleSaveTool(scopes).ExecuteAsync(
            J("""{"name":"Root disk","dataset":"disk","field":"usedPercent","operator":"gte","threshold":90,"severity":"critical","filter":{"mount":"/"},"forMinutes":5}"""), ctx, default);
        ok.IsError.Should().BeFalse();
        ok.Content.Should().Contain("Created rule 'Root disk'").And.Contain("disk.usedPercent ≥ 90 for 5 min").And.Contain("mount=/");

        var list = (ToolCompleted)await new AlertListTool(scopes).ExecuteAsync(J("{}"), ctx, default);
        list.Content.Should().Contain("Rules (1)").And.Contain("Root disk [critical, llm]").And.Contain("Firing now (0)");

        (await new AlertRuleDeleteTool(scopes).ExecuteAsync(J("""{"name":"Root disk"}"""), ctx, default))
            .Should().BeOfType<ToolCompleted>().Which.Content.Should().Contain("Deleted");

        var noAgent = (ToolCompleted)await new AlertRuleSaveTool(scopes).ExecuteAsync(J("{}"), new ToolExecutionContext(false), default);
        noAgent.IsError.Should().BeTrue();
    }
}

/// <summary>The same queries against real SQLite, where DateTime comparison/ordering and value converters are
/// stricter than the in-memory provider.</summary>
public class AlertsRepositorySqliteTests
{
    private static (AppDbContext Db, SqliteConnection Conn) Open()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    [Fact]
    public async Task Events_Order_Filter_Prune_AndRoundTripUtc_OnRealSqlite()
    {
        var (db, conn) = Open();
        await using var _ = db;
        await using var __ = conn;

        var agent = await TestSeed.AgentAsync(db, "a", null, "p", "ollama", "m", null, [], [], null,
            AgentScheduleKind.Manual, null, null, null, true, default, null, AgentType.DashboardAlerts, "{}");
        var repo = new AlertsRepository(db);
        var rule = new AlertRule { AgentId = agent.Id, Name = "r", Dataset = "system", Field = "cpuPercent", Operator = "gte" };
        db.AlertRules.Add(rule);

        var now = DateTime.UtcNow;
        AlertEvent Make(string key, AlertState state, DateTime fired, DateTime? resolved = null) => new()
        {
            RuleId = rule.Id, AgentId = agent.Id, EntityKey = key, EntityLabel = key, State = state, Message = key,
            FirstSeen = fired, FiredAt = fired, LastSeen = fired, ResolvedAt = resolved
        };
        db.AlertEvents.Add(Make("old-firing", AlertState.Firing, now.AddHours(-5)));
        db.AlertEvents.Add(Make("new-firing", AlertState.Firing, now.AddHours(-1)));
        db.AlertEvents.Add(Make("recent-resolved", AlertState.Resolved, now.AddHours(-3), now.AddHours(-2)));
        db.AlertEvents.Add(Make("ancient-resolved", AlertState.Resolved, now.AddDays(-40), now.AddDays(-40)));
        db.AlertEvents.Add(Make("pending", AlertState.Pending, now));
        await db.SaveChangesAsync();

        var shown = await repo.GetEventsAsync(agent.Id, true, now.AddDays(-7), default);
        shown.Select(e => e.EntityKey).Should().Equal("new-firing", "old-firing", "recent-resolved");
        shown[0].FiredAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

        (await new AlertsService(db, repo, new AgentsRepository(db)).PruneResolvedAsync(agent.Id, now.AddDays(-30), default)).Should().Be(1);
        (await repo.GetOpenEventsAsync(rule.Id, default)).Should().HaveCount(3); // 2 firing + pending
    }
}
