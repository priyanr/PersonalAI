using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Secrets;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Infrastructure;

public class SecretsRepositoryTests
{
    private static async Task<AppDbContext> NewContextWith(params Secret[] secrets)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Secrets.AddRange(secrets);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task GetValueAsync_UnknownKey_ReturnsNull()
    {
        await using var db = await NewContextWith(new Secret { Key = "A", Value = "1" });

        (await new SecretsRepository(db).GetValueAsync("B", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetValuesAsync_ReturnsOnlyRequestedKeysThatHaveAValue()
    {
        await using var db = await NewContextWith(
            new Secret { Key = "A", Value = "1" },
            new Secret { Key = "B", Value = "" },
            new Secret { Key = "C", Value = "3" });

        var values = await new SecretsRepository(db).GetValuesAsync(["A", "B", "MISSING"], CancellationToken.None);

        values.Should().Equal(new Dictionary<string, string> { ["A"] = "1" });
    }

    [Fact]
    public async Task GetAllAsync_IsOrderedByKey()
    {
        await using var db = await NewContextWith(new Secret { Key = "B" }, new Secret { Key = "A" });

        (await new SecretsRepository(db).GetAllAsync(CancellationToken.None)).Select(s => s.Key).Should().Equal("A", "B");
    }
}
