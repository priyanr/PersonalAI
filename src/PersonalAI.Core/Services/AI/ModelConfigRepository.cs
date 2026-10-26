using PersonalAI.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.AI;

public interface IModelConfigRepository
{
    /// <summary>Every saved model setting, keyed by provider, then model name, then field name.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>> GetAllAsync(CancellationToken ct);
}

public class ModelConfigRepository(AppDbContext db) : IModelConfigRepository
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>> GetAllAsync(CancellationToken ct)
    {
        var all = await db.ModelConfigs.AsNoTracking().ToListAsync(ct);
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
}
