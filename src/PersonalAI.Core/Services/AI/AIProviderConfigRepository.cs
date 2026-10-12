using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.AI;

public class AIProviderConfigRepository(AppDbContext db) : IAIProviderConfigRepository
{
    public async Task<IReadOnlyDictionary<string, string?>> GetValuesAsync(string providerKey, CancellationToken ct) =>
        await db.AIProviderConfigs
            .Where(c => c.ProviderKey == providerKey)
            .ToDictionaryAsync(c => c.FieldName, c => c.Value, ct);

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct)
    {
        var all = await db.AIProviderConfigs.ToListAsync(ct);
        return all
            .GroupBy(c => c.ProviderKey)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string?>)g.ToDictionary(c => c.FieldName, c => c.Value));
    }

    public async Task SetValuesAsync(string providerKey, IReadOnlyDictionary<string, string?> updates, CancellationToken ct)
    {
        var existing = await db.AIProviderConfigs
            .Where(c => c.ProviderKey == providerKey)
            .ToDictionaryAsync(c => c.FieldName, ct);

        foreach (var (fieldName, value) in updates)
        {
            if (existing.TryGetValue(fieldName, out var row))
            {
                row.Value = value;
            }
            else
            {
                db.AIProviderConfigs.Add(new AIProviderConfig { ProviderKey = providerKey, FieldName = fieldName, Value = value });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
