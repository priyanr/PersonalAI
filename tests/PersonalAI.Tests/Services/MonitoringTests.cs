using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents;
using PersonalAI.Core.Services.Monitoring;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Ssh;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Services;

public class DatasetRepositoryTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static NewDataPoint Point(string entity, DateTime ts, string json = "{}", string dataset = "system", DatasetKind kind = DatasetKind.Timeseries) =>
        new(dataset, kind, entity, ts, json);

    [Fact]
    public async Task QueryAsync_LatestPerEntity_ReturnsOnlyNewestEach()
    {
        await using var db = NewDb();
        var repo = new DatasetRepository(db);
        var data = new DatasetService(db, repo, new AgentsRepository(db));
        var agent = Guid.NewGuid();
        var t = DateTime.UtcNow;
        await data.AddPointsAsync(agent, [Point("a", t.AddMinutes(-10), "{\"v\":1}"), Point("a", t, "{\"v\":2}"), Point("b", t.AddMinutes(-5), "{\"v\":3}")], CancellationToken.None);

        var rows = await repo.QueryAsync(agent, "system", null, null, null, true, 100, CancellationToken.None);

        rows!.Select(r => r.ValuesJson).Should().Equal("{\"v\":2}", "{\"v\":3}");
    }

    [Fact]
    public async Task QueryAsync_WindowOverLimit_KeepsNewestInChronologicalOrder()
    {
        await using var db = NewDb();
        var repo = new DatasetRepository(db);
        var data = new DatasetService(db, repo, new AgentsRepository(db));
        var agent = Guid.NewGuid();
        var t = DateTime.UtcNow;
        await data.AddPointsAsync(agent, [.. Enumerable.Range(0, 10).Select(i => Point("a", t.AddMinutes(-10 + i), $"{{\"i\":{i}}}"))], CancellationToken.None);

        var rows = await repo.QueryAsync(agent, "system", null, null, t.AddMinutes(-20), false, 3, CancellationToken.None);

        rows!.Select(r => r.ValuesJson).Should().Equal("{\"i\":7}", "{\"i\":8}", "{\"i\":9}");
    }

    [Fact]
    public async Task QueryAsync_UnknownDatasetIsNull_AndPrefixFilters()
    {
        await using var db = NewDb();
        var repo = new DatasetRepository(db);
        var data = new DatasetService(db, repo, new AgentsRepository(db));
        var agent = Guid.NewGuid();
        await data.AddPointsAsync(agent, [Point("t1|/", DateTime.UtcNow), Point("t2|/", DateTime.UtcNow)], CancellationToken.None);

        (await repo.QueryAsync(agent, "nope", null, null, null, false, 10, CancellationToken.None)).Should().BeNull();
        (await repo.QueryAsync(agent, "system", null, "t1", null, false, 10, CancellationToken.None))!.Should().ContainSingle();
    }

    [Fact]
    public async Task PruneAsync_RemovesOldPoints_InEveryDatasetKind_IncludingSnapshots()
    {
        await using var db = NewDb();
        var repo = new DatasetRepository(db);
        var data = new DatasetService(db, repo, new AgentsRepository(db));
        var agent = Guid.NewGuid();
        var old = DateTime.UtcNow.AddDays(-60);
        await data.AddPointsAsync(agent,
        [
            Point("a", old), Point("a", DateTime.UtcNow),
            Point("a", old.AddDays(-1), dataset: "container", kind: DatasetKind.Snapshot), Point("a", old, dataset: "container", kind: DatasetKind.Snapshot)
        ], CancellationToken.None);

        var removed = await data.PruneAsync(agent, DateTime.UtcNow.AddDays(-30), CancellationToken.None);

        // 1 old system point + both old container points: a snapshot entity that stopped reporting is not kept forever
        removed.Should().Be(3);
        (await repo.QueryAsync(agent, "container", null, null, null, true, 10, CancellationToken.None))!.Should().BeEmpty();
        (await repo.QueryAsync(agent, "system", null, null, null, true, 10, CancellationToken.None))!.Should().ContainSingle();
    }

    [Fact]
    public async Task PruneAsync_RemovesABacklogLargerThanOneBatch_AndKeepsWhatIsRecent()
    {
        await using var db = NewDb();
        var repo = new DatasetRepository(db);
        var data = new DatasetService(db, repo, new AgentsRepository(db));
        var agent = Guid.NewGuid();
        var old = DateTime.UtcNow.AddDays(-60);
        var points = Enumerable.Range(0, 11_000).Select(i => Point($"e{i % 50}", old.AddSeconds(i))).ToList();
        points.Add(Point("e0", DateTime.UtcNow));
        await data.AddPointsAsync(agent, points, CancellationToken.None);

        var removed = await data.PruneAsync(agent, DateTime.UtcNow.AddDays(-30), CancellationToken.None);

        removed.Should().Be(11_000);
        (await repo.QueryAsync(agent, "system", null, null, null, false, 100, CancellationToken.None))!.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureDatasetsAsync_IsIdempotent()
    {
        await using var db = NewDb();
        var repo = new DatasetRepository(db);
        var data = new DatasetService(db, repo, new AgentsRepository(db));
        var agent = Guid.NewGuid();

        await data.EnsureDatasetsAsync(agent, new[] { DataSourceStatus.Definition }.ToList(), CancellationToken.None);
        await data.EnsureDatasetsAsync(agent, new[] { DataSourceStatus.Definition }.ToList(), CancellationToken.None);

        (await repo.ListAsync(agent, CancellationToken.None)).Should().HaveCount(1);
    }
}

public class DataQueryBucketTests
{
    [Fact]
    public void DataQueryBucket_AveragesNumericFieldsPerEntityBucket()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var rows = new List<DataPointRow>
        {
            new("a", t.AddMinutes(1), "{\"cpu\":10,\"state\":\"x\"}"),
            new("a", t.AddMinutes(2), "{\"cpu\":30,\"state\":\"y\"}"),
            new("a", t.AddMinutes(11), "{\"cpu\":50,\"state\":\"z\"}")
        };

        var bucketed = DataQueryTool.Bucket(rows, TimeSpan.FromMinutes(10));

        bucketed.Should().HaveCount(2);
        bucketed[0].ValuesJson.Should().Contain("\"cpu\":20").And.Contain("\"state\":\"y\"");
        bucketed[1].ValuesJson.Should().Contain("\"cpu\":50");
    }
}
