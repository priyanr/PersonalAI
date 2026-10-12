using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Services.AI;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Infrastructure;

public class AIProviderConfigRepositoryTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SetValuesAsync_ThenGetValuesAsync_RoundTrips()
    {
        await using var db = NewContext();
        var repo = new AIProviderConfigRepository(db);

        await repo.SetValuesAsync("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-abc", ["BaseUrl"] = "https://api.openai.com/v1" }, CancellationToken.None);
        var values = await repo.GetValuesAsync("openai", CancellationToken.None);

        values.Should().Contain(new KeyValuePair<string, string?>("ApiKey", "sk-abc"));
        values.Should().Contain(new KeyValuePair<string, string?>("BaseUrl", "https://api.openai.com/v1"));
    }

    [Fact]
    public async Task SetValuesAsync_CalledTwice_UpdatesExistingRowRatherThanDuplicating()
    {
        await using var db = NewContext();
        var repo = new AIProviderConfigRepository(db);

        await repo.SetValuesAsync("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-old" }, CancellationToken.None);
        await repo.SetValuesAsync("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-new" }, CancellationToken.None);

        var values = await repo.GetValuesAsync("openai", CancellationToken.None);
        values.Should().ContainSingle().Which.Value.Should().Be("sk-new");
    }

    [Fact]
    public async Task SetValuesAsync_OnlyTouchesKeysPresentInUpdate()
    {
        await using var db = NewContext();
        var repo = new AIProviderConfigRepository(db);

        await repo.SetValuesAsync("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-abc", ["BaseUrl"] = "https://api.openai.com/v1" }, CancellationToken.None);
        await repo.SetValuesAsync("openai", new Dictionary<string, string?> { ["BaseUrl"] = "https://custom" }, CancellationToken.None);

        var values = await repo.GetValuesAsync("openai", CancellationToken.None);
        values["ApiKey"].Should().Be("sk-abc");
        values["BaseUrl"].Should().Be("https://custom");
    }

    [Fact]
    public async Task GetAllAsync_GroupsRowsByProviderKey()
    {
        await using var db = NewContext();
        var repo = new AIProviderConfigRepository(db);

        await repo.SetValuesAsync("ollama", new Dictionary<string, string?> { ["BaseUrl"] = "http://localhost:11434" }, CancellationToken.None);
        await repo.SetValuesAsync("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-abc" }, CancellationToken.None);

        var all = await repo.GetAllAsync(CancellationToken.None);

        all.Should().ContainKey("ollama");
        all.Should().ContainKey("openai");
        all["ollama"]["BaseUrl"].Should().Be("http://localhost:11434");
        all["openai"]["ApiKey"].Should().Be("sk-abc");
    }

    [Fact]
    public async Task GetValuesAsync_UnknownProvider_ReturnsEmpty()
    {
        await using var db = NewContext();
        var repo = new AIProviderConfigRepository(db);

        var values = await repo.GetValuesAsync("does-not-exist", CancellationToken.None);
        values.Should().BeEmpty();
    }
}
