using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Skills;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Infrastructure;

public class SkillsRepositoryTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        await using var db = NewContext();

        (await new SkillsRepository(db).GetAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_OrdersByMostRecentlyUpdatedFirst()
    {
        await using var db = NewContext();
        db.Skills.Add(new Skill { Name = "older", Prompt = "p", ModelName = "m", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        db.Skills.Add(new Skill { Name = "newer", Prompt = "p", ModelName = "m", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(CancellationToken.None);

        var all = await new SkillsRepository(db).GetAllAsync(CancellationToken.None);

        all.Select(s => s.Name).Should().ContainInOrder("newer", "older");
    }
}
