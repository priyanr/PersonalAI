using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities.Enums;
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
    public async Task CreateAsync_ThenGetAsync_RoundTrips()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);

        var created = await repo.CreateAsync(
            "Daily digest", "Summarizes overnight activity", "You are an orchestrator.",
            "ollama", "llama3.1:8b", 8192, [Guid.NewGuid()], [], null, AgentScheduleKind.Manual,
            null, null, null, true, CancellationToken.None);

        var fetched = await repo.GetAsync(created.Id, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Daily digest");
        fetched.Prompt.Should().Be("You are an orchestrator.");
        fetched.ProviderKey.Should().Be("ollama");
        fetched.ModelName.Should().Be("llama3.1:8b");
        fetched.NumCtx.Should().Be(8192);
        fetched.ScheduleKind.Should().Be(AgentScheduleKind.Manual);
        fetched.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_IntervalSchedule_ComputesNextRunAt()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);
        var before = DateTimeOffset.UtcNow;

        var created = await repo.CreateAsync(
            "a", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Interval,
            null, 15, null, true, CancellationToken.None);

        created.NextRunAt.Should().NotBeNull();
        created.NextRunAt.Should().BeCloseTo(before.AddMinutes(15), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateAsync_ManualSchedule_LeavesNextRunAtNull()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);

        var created = await repo.CreateAsync(
            "a", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Manual,
            null, null, null, true, CancellationToken.None);

        created.NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ExistingAgent_UpdatesFieldsAndRearmsSchedule()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);
        var agent = await repo.CreateAsync(
            "old", null, "old prompt", "ollama", "m", null, [], [], null, AgentScheduleKind.Manual,
            null, null, null, true, CancellationToken.None);
        var skillId = Guid.NewGuid();

        var updated = await repo.UpdateAsync(
            agent.Id, "new", "desc", "new prompt", "openai", "gpt-4o", 4096, [skillId], ["gmail"], 20,
            AgentScheduleKind.Interval, null, 60, null, true, CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("new");
        updated.ModelName.Should().Be("gpt-4o");
        updated.ScheduleKind.Should().Be(AgentScheduleKind.Interval);
        updated.EnabledSkillIdsJson.Should().Contain(skillId.ToString());
        updated.EnabledToolsJson.Should().Contain("gmail");
        updated.MaxLlmCalls.Should().Be(20);
        updated.NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);

        var updated = await repo.UpdateAsync(
            Guid.NewGuid(), "n", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Manual,
            null, null, null, true, CancellationToken.None);

        updated.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ExistingAgent_RemovesIt()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);
        var agent = await repo.CreateAsync(
            "n", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Manual,
            null, null, null, true, CancellationToken.None);

        (await repo.DeleteAsync(agent.Id, CancellationToken.None)).Should().BeTrue();
        (await repo.GetAsync(agent.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);

        (await repo.DeleteAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
    }

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

    [Fact]
    public async Task SetRunOutcomeAsync_UpdatesLastRunAtAndNextRunAt()
    {
        await using var db = NewContext();
        var repo = new AgentsRepository(db);
        var agent = await repo.CreateAsync(
            "n", null, "p", "ollama", "m", null, [], [], null, AgentScheduleKind.Manual,
            null, null, null, true, CancellationToken.None);
        var lastRun = DateTimeOffset.UtcNow;
        var nextRun = lastRun.AddHours(1);

        await repo.SetRunOutcomeAsync(agent.Id, lastRun, nextRun, CancellationToken.None);

        var fetched = await repo.GetAsync(agent.Id, CancellationToken.None);
        fetched!.LastRunAt.Should().Be(lastRun);
        fetched.NextRunAt.Should().Be(nextRun);
    }
}
