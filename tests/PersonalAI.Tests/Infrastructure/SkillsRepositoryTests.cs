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
    public async Task CreateAsync_ThenGetAsync_RoundTrips()
    {
        await using var db = NewContext();
        var repo = new SkillsRepository(db);

        var created = await repo.CreateAsync(
            "Weather digest", "Summarizes the weather", "You summarize today's weather.",
            "ollama", "llama3.1:8b", 8192, ["web_search"], ["WEATHER_API_KEY"], CancellationToken.None);

        var fetched = await repo.GetAsync(created.Id, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Weather digest");
        fetched.Prompt.Should().Be("You summarize today's weather.");
        fetched.ProviderKey.Should().Be("ollama");
        fetched.ModelName.Should().Be("llama3.1:8b");
        fetched.NumCtx.Should().Be(8192);
        fetched.EnabledToolsJson.Should().Contain("web_search");
        fetched.SecretKeysJson.Should().Contain("WEATHER_API_KEY");
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        await using var db = NewContext();
        var repo = new SkillsRepository(db);

        (await repo.GetAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_ExistingSkill_UpdatesFieldsAndTimestamp()
    {
        await using var db = NewContext();
        var repo = new SkillsRepository(db);
        var skill = await repo.CreateAsync(
            "Old name", null, "old prompt", "ollama", "llama3.1:8b", null, [], [], CancellationToken.None);
        var originalUpdatedAt = skill.UpdatedAt;

        var updated = await repo.UpdateAsync(
            skill.Id, "New name", "new description", "new prompt", "openai", "gpt-4o", 4096,
            ["web_search", "read_file"], ["API_KEY"], CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("New name");
        updated.Description.Should().Be("new description");
        updated.Prompt.Should().Be("new prompt");
        updated.ProviderKey.Should().Be("openai");
        updated.ModelName.Should().Be("gpt-4o");
        updated.NumCtx.Should().Be(4096);
        updated.EnabledToolsJson.Should().Contain("read_file");
        updated.UpdatedAt.Should().BeOnOrAfter(originalUpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull()
    {
        await using var db = NewContext();
        var repo = new SkillsRepository(db);

        var updated = await repo.UpdateAsync(
            Guid.NewGuid(), "name", null, "prompt", "ollama", "model", null, [], [], CancellationToken.None);

        updated.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ExistingSkill_RemovesIt()
    {
        await using var db = NewContext();
        var repo = new SkillsRepository(db);
        var skill = await repo.CreateAsync(
            "name", null, "prompt", "ollama", "model", null, [], [], CancellationToken.None);

        var deleted = await repo.DeleteAsync(skill.Id, CancellationToken.None);

        deleted.Should().BeTrue();
        (await repo.GetAsync(skill.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse()
    {
        await using var db = NewContext();
        var repo = new SkillsRepository(db);

        (await repo.DeleteAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_OrdersByMostRecentlyUpdatedFirst()
    {
        await using var db = NewContext();
        db.Skills.Add(new Skill { Name = "older", Prompt = "p", ModelName = "m", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        db.Skills.Add(new Skill { Name = "newer", Prompt = "p", ModelName = "m", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(CancellationToken.None);
        var repo = new SkillsRepository(db);

        var all = await repo.GetAllAsync(CancellationToken.None);

        all.Select(s => s.Name).Should().ContainInOrder("newer", "older");
    }
}
