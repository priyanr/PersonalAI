using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.AI;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Tests.Infrastructure;

public class AIProviderConfigRepositoryTests
{
    [Fact]
    public async Task GetAllAsync_GroupsRowsByProviderKey()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.AIProviderConfigs.Add(new AIProviderConfig { ProviderKey = "ollama", FieldName = "BaseUrl", Value = "http://localhost:11434" });
        db.AIProviderConfigs.Add(new AIProviderConfig { ProviderKey = "openai", FieldName = "ApiKey", Value = "sk-abc" });
        await db.SaveChangesAsync();

        var all = await new AIProviderConfigRepository(db).GetAllAsync(CancellationToken.None);

        all["ollama"]["BaseUrl"].Should().Be("http://localhost:11434");
        all["openai"]["ApiKey"].Should().Be("sk-abc");
    }
}
