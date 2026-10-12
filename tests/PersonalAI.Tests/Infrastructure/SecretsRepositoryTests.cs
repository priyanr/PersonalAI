using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Services.Secrets;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Infrastructure;

public class SecretsRepositoryTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task CreateAsync_ThenGetValueAsync_RoundTrips()
    {
        await using var db = NewContext();
        var repo = new SecretsRepository(db);

        await repo.CreateAsync("GITHUB_TOKEN", "ghp_abc", CancellationToken.None);
        var value = await repo.GetValueAsync("GITHUB_TOKEN", CancellationToken.None);

        value.Should().Be("ghp_abc");
    }

    [Fact]
    public async Task CreateAsync_DuplicateKey_Throws()
    {
        await using var db = NewContext();
        var repo = new SecretsRepository(db);

        await repo.CreateAsync("GITHUB_TOKEN", "ghp_abc", CancellationToken.None);

        var act = () => repo.CreateAsync("GITHUB_TOKEN", "ghp_other", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateValueAsync_ExistingKey_UpdatesValue()
    {
        await using var db = NewContext();
        var repo = new SecretsRepository(db);
        await repo.CreateAsync("GITHUB_TOKEN", "ghp_old", CancellationToken.None);

        var updated = await repo.UpdateValueAsync("GITHUB_TOKEN", "ghp_new", CancellationToken.None);

        updated.Should().BeTrue();
        (await repo.GetValueAsync("GITHUB_TOKEN", CancellationToken.None)).Should().Be("ghp_new");
    }

    [Fact]
    public async Task UpdateValueAsync_UnknownKey_ReturnsFalse()
    {
        await using var db = NewContext();
        var repo = new SecretsRepository(db);

        var updated = await repo.UpdateValueAsync("does-not-exist", "value", CancellationToken.None);

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ExistingKey_RemovesIt()
    {
        await using var db = NewContext();
        var repo = new SecretsRepository(db);
        await repo.CreateAsync("GITHUB_TOKEN", "ghp_abc", CancellationToken.None);

        var deleted = await repo.DeleteAsync("GITHUB_TOKEN", CancellationToken.None);

        deleted.Should().BeTrue();
        (await repo.GetValueAsync("GITHUB_TOKEN", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownKey_ReturnsFalse()
    {
        await using var db = NewContext();
        var repo = new SecretsRepository(db);

        var deleted = await repo.DeleteAsync("does-not-exist", CancellationToken.None);

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsSecretsOrderedByKey()
    {
        await using var db = NewContext();
        var repo = new SecretsRepository(db);
        await repo.CreateAsync("ZKEY", "z", CancellationToken.None);
        await repo.CreateAsync("AKEY", "a", CancellationToken.None);

        var all = await repo.GetAllAsync(CancellationToken.None);

        all.Select(s => s.Key).Should().ContainInOrder("AKEY", "ZKEY");
    }
}
