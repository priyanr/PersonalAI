using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Services.Agents;
using Microsoft.EntityFrameworkCore;
// Alias needed: PersonalAI.Core.Services.Agent (the ChatAgentService namespace) collides with the
// Models.Entities.Agent entity type's simple name once PersonalAI.Core.Services.Agents is imported.
using AgentEntity = PersonalAI.Core.Models.Entities.Agent;

namespace PersonalAI.Tests.Infrastructure;

public class AgentsRepositoryTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);


    [Fact]
    public async Task GetAllAsync_OrdersByMostRecentlyUpdatedFirst()
    {
        await using var db = NewContext();
        db.Agents.Add(new AgentEntity { Name = "older", Prompt = "p", ModelName = "m", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        db.Agents.Add(new AgentEntity { Name = "newer", Prompt = "p", ModelName = "m", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(CancellationToken.None);
        var repo = new AgentsRepository(db);

        var all = await repo.GetAllAsync(CancellationToken.None);

        all.Select(a => a.Name).Should().ContainInOrder("newer", "older");
    }

    [Fact]
    public async Task GetDueAsync_ReturnsOnlyEnabledAgentsWithPastNextRunAt()
    {
        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;
        db.Agents.Add(new AgentEntity { Name = "due", Prompt = "p", ModelName = "m", Enabled = true, NextRunAt = now.AddMinutes(-1) });
        db.Agents.Add(new AgentEntity { Name = "future", Prompt = "p", ModelName = "m", Enabled = true, NextRunAt = now.AddMinutes(5) });
        db.Agents.Add(new AgentEntity { Name = "disabled", Prompt = "p", ModelName = "m", Enabled = false, NextRunAt = now.AddMinutes(-1) });
        db.Agents.Add(new AgentEntity { Name = "manual", Prompt = "p", ModelName = "m", Enabled = true, NextRunAt = null });
        await db.SaveChangesAsync(CancellationToken.None);
        var repo = new AgentsRepository(db);

        var due = await repo.GetDueAsync(now, CancellationToken.None);

        due.Select(a => a.Name).Should().Equal("due");
    }
}
