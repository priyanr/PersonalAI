using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.AI;

public class ModelConfigRepository(AppDbContext db) : IModelConfigRepository
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>> GetAllAsync(CancellationToken ct)
    {
        var all = await db.ModelConfigs.ToListAsync(ct);
        return all
            .GroupBy(c => c.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                p => p.Key,
                p => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>)p
                    .GroupBy(c => c.ModelName)
                    .ToDictionary(
                        m => m.Key,
                        m => (IReadOnlyDictionary<string, string?>)m.ToDictionary(c => c.FieldName, c => c.Value)),
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task ReplaceAsync(string providerKey, string modelName, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var existing = await db.ModelConfigs
            .Where(c => c.ProviderKey == providerKey && c.ModelName == modelName)
            .ToListAsync(ct);
        db.ModelConfigs.RemoveRange(existing);

        foreach (var (fieldName, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            db.ModelConfigs.Add(new ModelConfig { ProviderKey = providerKey, ModelName = modelName, FieldName = fieldName, Value = value });
        }

        await db.SaveChangesAsync(ct);
    }
}
