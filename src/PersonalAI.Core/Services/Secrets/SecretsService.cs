using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Services.Secrets;

/// <summary>The secrets vault behind /api/secrets. A value is never returned, only whether one is set.</summary>
public class SecretsService(AppDbContext db, ISecretsRepository secrets)
{
    public async Task<List<SecretDto>> GetAll(CancellationToken ct)
    {
        return (await secrets.GetAllAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<SecretDto> Create(CreateSecretRequest request, CancellationToken ct)
    {
        var key = request.Key?.Trim() ?? "";
        if (key.Length == 0) throw new InvalidRequestException("Key is required.");
        if (string.IsNullOrEmpty(request.Value)) throw new InvalidRequestException("Value is required.");
        if (await secrets.GetAsync(key, ct) is not null)
            throw new ConflictException($"A secret with key '{key}' already exists.");

        var secret = new Secret { Key = key, Value = request.Value, UpdatedAt = DateTimeOffset.UtcNow };
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct);
        return ToDto(secret);
    }

    public async Task<SecretDto> Update(string key, UpdateSecretRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Value)) throw new InvalidRequestException("Value is required.");
        var secret = await secrets.GetAsync(key, ct) ?? throw new NotFoundException($"No secret with key '{key}'.");

        secret.Value = request.Value;
        secret.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(secret);
    }

    public async Task Delete(string key, CancellationToken ct)
    {
        var secret = await secrets.GetAsync(key, ct) ?? throw new NotFoundException($"No secret with key '{key}'.");
        db.Secrets.Remove(secret);
        await db.SaveChangesAsync(ct);
    }

    private static SecretDto ToDto(Secret secret)
    {
        return new SecretDto(secret.Key, !string.IsNullOrEmpty(secret.Value), secret.UpdatedAt);
    }
}
