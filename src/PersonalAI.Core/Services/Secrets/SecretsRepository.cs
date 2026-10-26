using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Secrets;

public interface ISecretsRepository
{
    Task<List<Secret>> GetAllAsync(CancellationToken ct);

    /// <summary>The tracked secret, or null if no secret has that key.</summary>
    Task<Secret?> GetAsync(string key, CancellationToken ct);

    Task<string?> GetValueAsync(string key, CancellationToken ct);

    /// <summary>The set values of <paramref name="keys"/>; keys with no secret or an empty value are left out.</summary>
    Task<Dictionary<string, string>> GetValuesAsync(IReadOnlyList<string> keys, CancellationToken ct);
}

public class SecretsRepository(AppDbContext db) : ISecretsRepository
{
    public async Task<List<Secret>> GetAllAsync(CancellationToken ct)
    {
        return await db.Secrets.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);
    }

    public async Task<Secret?> GetAsync(string key, CancellationToken ct)
    {
        return await db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken ct)
    {
        return await db.Secrets.Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    public async Task<Dictionary<string, string>> GetValuesAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        return await db.Secrets.AsNoTracking()
            .Where(s => keys.Contains(s.Key) && s.Value != null && s.Value != "")
            .ToDictionaryAsync(s => s.Key, s => s.Value!, StringComparer.Ordinal, ct);
    }
}
