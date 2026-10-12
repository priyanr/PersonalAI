using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Infrastructure;

public class AgentRunsRepositoryTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task CreateAsync_ThenGetAsync_RoundTrips()
    {
        await using var db = NewContext();
        var repo = new AgentRunsRepository(db);
        var agentId = Guid.NewGuid();

        var run = await repo.CreateAsync(agentId, AgentRunTrigger.Manual, CancellationToken.None);

        var fetched = await repo.GetAsync(run.Id, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.AgentId.Should().Be(agentId);
        fetched.Trigger.Should().Be(AgentRunTrigger.Manual);
        fetched.Status.Should().Be(AgentRunStatus.Running);
        fetched.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_SetsStatusSummaryTranscriptAndCompletedAt()
    {
        await using var db = NewContext();
        var repo = new AgentRunsRepository(db);
        var run = await repo.CreateAsync(Guid.NewGuid(), AgentRunTrigger.Scheduled, CancellationToken.None);

        await repo.CompleteAsync(run.Id, AgentRunStatus.Succeeded, "All done", "[{\"kind\":\"text\"}]", CancellationToken.None);

        var fetched = await repo.GetAsync(run.Id, CancellationToken.None);
        fetched!.Status.Should().Be(AgentRunStatus.Succeeded);
        fetched.Summary.Should().Be("All done");
        fetched.TranscriptJson.Should().Contain("text");
        fetched.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetForAgentAsync_ReturnsNewestFirstAndRespectsLimit()
    {
        await using var db = NewContext();
        var repo = new AgentRunsRepository(db);
        var agentId = Guid.NewGuid();
        var other = Guid.NewGuid();

        var run1 = await repo.CreateAsync(agentId, AgentRunTrigger.Manual, CancellationToken.None);
        await Task.Delay(5);
        var run2 = await repo.CreateAsync(agentId, AgentRunTrigger.Manual, CancellationToken.None);
        await Task.Delay(5);
        var run3 = await repo.CreateAsync(agentId, AgentRunTrigger.Manual, CancellationToken.None);
        await repo.CreateAsync(other, AgentRunTrigger.Manual, CancellationToken.None);

        var runs = await repo.GetForAgentAsync(agentId, 2, CancellationToken.None);

        runs.Should().HaveCount(2);
        runs.Select(r => r.Id).Should().Equal(run3.Id, run2.Id);
        runs.Should().NotContain(r => r.Id == run1.Id);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        await using var db = NewContext();
        var repo = new AgentRunsRepository(db);

        (await repo.GetAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }
}
