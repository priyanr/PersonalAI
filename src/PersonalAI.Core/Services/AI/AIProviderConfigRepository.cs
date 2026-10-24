using PersonalAI.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.AI;

public interface IAIProviderConfigRepository
{
    /// <summary>Every stored value, keyed by provider, then field name.</summary>
    Task<Dictionary<string, Dictionary<string, string?>>> GetAllAsync(CancellationToken ct);
}

public class AIProviderConfigRepository(AppDbContext db) : IAIProviderConfigRepository
{
    public async Task<Dictionary<string, Dictionary<string, string?>>> GetAllAsync(CancellationToken ct)
    {
        var all = await db.AIProviderConfigs.AsNoTracking().ToListAsync(ct);
        return all
            .GroupBy(c => c.ProviderKey)
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.FieldName, c => c.Value));
    }
}
