using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Monitoring;
using PersonalAI.Core.Services.Monitoring.Dashboards;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Services;

public class DashboardSpecValidatorTests
{
    private static List<string> Errors(string json)
    {
        var (spec, error) = DashboardSpecJson.Parse(json);
        spec.Should().NotBeNull(error);
        return DashboardSpecValidator.Validate(spec!);
    }

    [Fact]
    public void Valid_Spec_HasNoErrors() =>
        Errors("""{"title":"T","widgets":[{"id":"cpu","type":"timeseries","query":{"dataset":"system","field":"cpuPercent"},"options":{"warn":70,"crit":90}}]}""")
            .Should().BeEmpty();

    [Fact]
    public void UnknownProperty_IsRejectedWithItsName()
    {
        var (spec, error) = DashboardSpecJson.Parse("""{"title":"T","widgets":[{"id":"a","type":"stat","query":{"dataset":"s","field":"f","windowMins":5}}]}""");

        spec.Should().BeNull();
        error.Should().Contain("windowMins");
    }

    [Fact]
    public void MultipleProblems_AreAllReported()
    {
        var errors = Errors("""
            {"title":"","widgets":[
              {"id":"a","type":"stat","query":{"dataset":"s"}},
              {"id":"a","type":"pie"},
              {"id":"Bad Id","type":"markdown"},
              {"id":"t","type":"table","query":{"dataset":"s"}},
              {"id":"g","type":"gauge","query":{"dataset":"s","field":"f","agg":"median"},"options":{"warn":90,"crit":70}}
            ]}
            """);

        errors.Should().Contain(e => e.Contains("title is required"))
            .And.Contain(e => e.Contains("query.field is required"))
            .And.Contain(e => e.Contains("type must be one of"))
            .And.Contain(e => e.Contains("widgets[2].id"))
            .And.Contain(e => e.Contains("markdown widgets need 'text'"))
            .And.Contain(e => e.Contains("options.columns"))
            .And.Contain(e => e.Contains("query.agg must be one of"))
            .And.Contain(e => e.Contains("warn must not exceed"));
    }

    [Fact]
    public void LowerIsWorse_FlipsThresholdOrdering()
    {
        Errors("""{"title":"T","widgets":[{"id":"d","type":"gauge","query":{"dataset":"s","field":"f"},"options":{"warn":20,"crit":10,"lowerIsWorse":true}}]}""")
            .Should().BeEmpty();
    }
}

public class DashboardServiceTests
{
    private static (ServiceProvider Provider, Guid AgentId) Build()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<DashboardService>();
        return (services.BuildServiceProvider(), Guid.NewGuid());
    }

    private static async Task SeedAsync(ServiceProvider provider, Guid agentId)
    {
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDatasetRepository>();
        var now = DateTime.UtcNow;
        var points = new List<NewDataPoint>();
        foreach (var (target, cpu, up) in new[] { ("web-1", 20.0, true), ("web-2", 95.0, true), ("db-1", 50.0, false) })
        {
            for (var i = 0; i < 6; i++)
                points.Add(new("system", DatasetKind.Timeseries, $"{target}", now.AddMinutes(-5 * (5 - i)),
                    JsonSerializer.Serialize(new { target, reachable = up, cpuPercent = cpu + i })));
            points.Add(new("disk", DatasetKind.Timeseries, $"{target}|/", now, JsonSerializer.Serialize(new { target, mount = "/", usedPercent = cpu / 2 })));
            points.Add(new("disk", DatasetKind.Timeseries, $"{target}|/data", now, JsonSerializer.Serialize(new { target, mount = "/data", usedPercent = 85.0 })));
        }
        await repo.AddPointsAsync(agentId, points, default);
    }

    private static async Task<Dictionary<string, object>> DataAsync(ServiceProvider provider, Guid agentId, string widgetsJson)
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DashboardService>();
        var validation = await service.ValidateAsync(agentId, $$"""{"title":"T","widgets":{{widgetsJson}}}""", default);
        validation.Errors.Should().BeEmpty();
        return await service.BuildDataAsync(agentId, validation.Spec!, default);
    }

    [Fact]
    public async Task Validate_UnknownDatasetFieldColumnFilterAndServer_AreAllReportedWithAlternatives()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DashboardService>();

        var v = await service.ValidateAsync(agent, """
            {"title":"T","widgets":[
              {"id":"a","type":"stat","query":{"dataset":"nope","field":"x"}},
              {"id":"b","type":"stat","query":{"dataset":"system","field":"cpu"}},
              {"id":"c","type":"table","query":{"dataset":"disk","filter":{"drive":"/"}},"options":{"columns":["usedPct"]}},
              {"id":"d","type":"stat","query":{"dataset":"system","field":"cpuPercent","target":"ghost"}}
            ]}
            """, default);

        v.IsValid.Should().BeFalse();
        v.Errors.Should().Contain(e => e.Contains("no dataset 'nope'") && e.Contains("system"))
            .And.Contain(e => e.Contains("field 'cpu' not found") && e.Contains("cpuPercent"))
            .And.Contain(e => e.Contains("filter key 'drive'"))
            .And.Contain(e => e.Contains("column 'usedPct'") && e.Contains("usedPercent"))
            .And.Contain(e => e.Contains("no connection named 'ghost'") && e.Contains("web-1"));
    }

    [Fact]
    public async Task Validate_EmptyDataset_SkipsFieldChecks()
    {
        var (provider, agent) = Build();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatasetRepository>()
            .EnsureDatasetsAsync(agent, new List<DatasetDefinition> { new("system", DatasetKind.Timeseries, null) }, default);

        var v = await scope.ServiceProvider.GetRequiredService<DashboardService>()
            .ValidateAsync(agent, """{"title":"T","widgets":[{"id":"a","type":"stat","query":{"dataset":"system","field":"cpuPercent"}}]}""", default);

        v.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Versions_SaveListRestore_AndOldOnesArePruned()
    {
        var (provider, agent) = Build();
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DashboardService>();
        DashboardSpec Spec(string title) => new(title, null, null, [new("m", "markdown", null, null, null, null, "hi")]);

        (await service.GetCurrentAsync(agent, default)).Should().BeNull();
        (await service.SaveAsync(agent, Spec("one"), "first", null, default)).Should().Be(1);
        (await service.SaveAsync(agent, Spec("two"), "second", null, default)).Should().Be(2);
        var restored = await service.RestoreAsync(agent, 1, default);

        restored.Should().Be(3);
        (await service.GetCurrentAsync(agent, default))!.SpecJson.Should().Contain("one");
        (await service.ListVersionsAsync(agent, default)).Select(v => v.Version).Should().Equal(3, 2, 1);
        (await service.ListVersionsAsync(agent, default))[0].Reason.Should().Be("Restored v1");
        (await service.RestoreAsync(agent, 99, default)).Should().BeNull();

        for (var i = 0; i < IDashboardRepository.KeepVersions; i++)
            await service.SaveAsync(agent, Spec($"v{i}"), null, null, default);
        var versions = await service.ListVersionsAsync(agent, default);
        versions.Should().HaveCount(IDashboardRepository.KeepVersions);
        versions[0].Version.Should().Be(3 + IDashboardRepository.KeepVersions);
    }

    [Fact]
    public async Task Stat_MaxAcrossServers_NamesTheWorstAndAppliesThresholds()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);

        var data = await DataAsync(provider, agent, """
            [{"id":"s","type":"stat","query":{"dataset":"system","field":"cpuPercent"},"options":{"unit":"%","warn":70,"crit":90}}]
            """);

        var stat = (StatData)data["s"];
        stat.Value.Should().Be(100); // web-2: 95 + i(5)
        stat.Detail.Should().Be("web-2");
        stat.Status.Should().Be("critical");
    }

    [Fact]
    public async Task Stat_CountOfBooleanField_AndGaugeDefaults()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);

        var data = await DataAsync(provider, agent, """
            [{"id":"up","type":"stat","query":{"dataset":"system","field":"reachable","agg":"count"}},
             {"id":"g","type":"gauge","query":{"dataset":"disk","field":"usedPercent","target":"web-1","filter":{"mount":"/"}}}]
            """);

        var up = (StatData)data["up"];
        up.Value.Should().Be(2);
        up.Detail.Should().Be("of 3");
        var gauge = (StatData)data["g"];
        gauge.Value.Should().Be(10);
        (gauge.Min, gauge.Max).Should().Be((0, 100));
    }

    [Fact]
    public async Task StatusGrid_BooleanField_MarksUnreachableCritical()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);

        var data = await DataAsync(provider, agent, """[{"id":"g","type":"statusGrid","query":{"dataset":"system","field":"reachable"}}]""");

        var grid = (GridData)data["g"];
        grid.Items.Select(i => (i.Label, i.Status, i.Text)).Should().Equal(("db-1", "critical", "Down"), ("web-1", "ok", "OK"), ("web-2", "ok", "OK"));
    }

    [Fact]
    public async Task Bar_RanksDiskMountsWithLabelsAndLimit()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);

        var data = await DataAsync(provider, agent, """
            [{"id":"b","type":"bar","query":{"dataset":"disk","field":"usedPercent"},"options":{"unit":"%","warn":80,"crit":90,"limit":2}}]
            """);

        var bar = (BarData)data["b"];
        bar.Items.Should().HaveCount(2);
        bar.Hidden.Should().Be(4);
        bar.Items[0].Value.Should().Be(85);
        bar.Items[0].Status.Should().Be("warning");
        bar.Items[0].Label.Should().EndWith("· /data");
    }

    [Fact]
    public async Task Table_ShowsRequestedColumnsPerEntity()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);

        var data = await DataAsync(provider, agent, """
            [{"id":"t","type":"table","query":{"dataset":"disk","filter":{"mount":"/data"}},"options":{"columns":["target","usedPercent"]}}]
            """);

        var table = (TableData)data["t"];
        table.Columns.Should().Equal("target", "usedPercent");
        table.Rows.Should().HaveCount(3);
        table.Rows[0].Should().Equal("db-1", 85.0);
    }

    [Fact]
    public async Task Timeseries_BucketsAveragesPerServer_AndCapsAtEightSeries()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);
        await using (var scope = provider.CreateAsyncScope())
        {
            var extra = Enumerable.Range(1, 8).Select(i => new NewDataPoint("system", DatasetKind.Timeseries, $"x{i}", DateTime.UtcNow,
                JsonSerializer.Serialize(new { target = $"x{i}", reachable = true, cpuPercent = (double)i }))).ToList();
            await scope.ServiceProvider.GetRequiredService<IDatasetRepository>().AddPointsAsync(agent, extra, default);
        }

        var data = await DataAsync(provider, agent, """
            [{"id":"c","type":"timeseries","query":{"dataset":"system","field":"cpuPercent","windowMinutes":60,"bucketMinutes":60},"options":{"warn":70,"crit":90}}]
            """);

        var ts = (TimeseriesData)data["c"];
        ts.Series.Should().HaveCount(DashboardDataBuilder.MaxSeries);
        ts.HiddenSeries.Should().Be(3); // 11 servers, 8 shown
        ts.Series.Select(s => s.Label).Should().Contain("web-2").And.NotContain("x1"); // worst-first selection
        (ts.Warn, ts.Crit).Should().Be((70, 90));
        ts.Series.Single(s => s.Label == "web-2").Points.Should().HaveCountLessOrEqualTo(2); // 60-min buckets
    }

    [Fact]
    public async Task Widget_WithBadQuery_ReturnsErrorWithoutFailingTheRest()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);
        await using var scope = provider.CreateAsyncScope();
        var builder = new DashboardDataBuilder(scope.ServiceProvider.GetRequiredService<IDatasetRepository>());
        var spec = new DashboardSpec("T", null, null,
        [
            new("bad", "stat", null, null, new("gone", "x", null, null, null, null, null), null, null),
            new("ok", "stat", null, null, new("system", "cpuPercent", null, null, null, null, null), null, null)
        ]);

        var data = await builder.BuildAsync(agent, spec, default);

        data["bad"].Should().BeOfType<WidgetError>();
        data["ok"].Should().BeOfType<StatData>();
    }

    [Fact]
    public async Task Tools_SaveRejectsBadSpecWithoutSaving_ThenSavesAndGetReturnsIt()
    {
        var (provider, agent) = Build();
        await SeedAsync(provider, agent);
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var save = new DashboardSaveTool(scopes);
        var get = new DashboardGetTool(scopes);
        var ctx = new ToolExecutionContext(false, new SandboxOwner(SandboxOwnerKind.AgentRun, Guid.NewGuid()), AgentId: agent);

        (await get.ExecuteAsync(JsonDocument.Parse("{}").RootElement, ctx, default)).Should().BeOfType<ToolCompleted>()
            .Which.Content.Should().Contain("No dashboard exists yet");

        var bad = (ToolCompleted)await save.ExecuteAsync(JsonDocument.Parse(
            """{"spec":{"title":"T","widgets":[{"id":"a","type":"stat","query":{"dataset":"system","field":"nope"}}]},"reason":"x"}""").RootElement, ctx, default);
        bad.IsError.Should().BeTrue();
        bad.Content.Should().Contain("Not saved").And.Contain("cpuPercent");
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<DashboardService>().GetCurrentAsync(agent, default)).Should().BeNull();

        var good = (ToolCompleted)await save.ExecuteAsync(JsonDocument.Parse(
            """{"spec":{"title":"Fleet","widgets":[{"id":"cpu","type":"stat","query":{"dataset":"system","field":"cpuPercent"}}]},"reason":"initial"}""").RootElement, ctx, default);
        good.IsError.Should().BeFalse();
        good.Content.Should().Contain("Saved dashboard version 1").And.Contain("\"cpu\"");

        var current = (ToolCompleted)await get.ExecuteAsync(JsonDocument.Parse("{}").RootElement, ctx, default);
        current.Content.Should().Contain("Version 1").And.Contain("initial").And.Contain("Fleet");

        var noAgent = (ToolCompleted)await save.ExecuteAsync(JsonDocument.Parse("{}").RootElement, new ToolExecutionContext(false), default);
        noAgent.IsError.Should().BeTrue();
    }
}
