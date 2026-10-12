using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Secrets;

public class SecretsRepository(AppDbContext db) : ISecretsRepository
{
    public async Task<IReadOnlyList<Secret>> GetAllAsync(CancellationToken ct) =>
        await db.Secrets.OrderBy(s => s.Key).ToListAsync(ct);

    public async Task<string?> GetValueAsync(string key, CancellationToken ct) =>
        (await db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task<Secret> CreateAsync(string key, string value, CancellationToken ct)
    {
        if (await db.Secrets.AnyAsync(s => s.Key == key, ct))
            throw new InvalidOperationException($"A secret with key '{key}' already exists.");

        var secret = new Secret { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow };
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct);
        return secret;
    }

    public async Task<bool> UpdateValueAsync(string key, string value, CancellationToken ct)
    {
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (secret is null) return false;

        secret.Value = value;
        secret.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (secret is null) return false;

        db.Secrets.Remove(secret);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
