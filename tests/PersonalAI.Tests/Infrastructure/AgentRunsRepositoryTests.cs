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
    public async Task GetForAgentAsync_ReturnsNewestFirstAndRespectsLimit()
    {
        await using var db = NewContext();
        var repo = new AgentRunsRepository(db);
        var agentId = Guid.NewGuid();
        var other = Guid.NewGuid();

        var run1 = await TestSeed.RunAsync(db, agentId, AgentRunTrigger.Manual);
        await Task.Delay(5);
        var run2 = await TestSeed.RunAsync(db, agentId, AgentRunTrigger.Manual);
        await Task.Delay(5);
        var run3 = await TestSeed.RunAsync(db, agentId, AgentRunTrigger.Manual);
        await TestSeed.RunAsync(db, other, AgentRunTrigger.Manual);

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
