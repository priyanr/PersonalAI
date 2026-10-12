using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

public interface ISecretsRepository
{
    Task<IReadOnlyList<Secret>> GetAllAsync(CancellationToken ct);

    Task<string?> GetValueAsync(string key, CancellationToken ct);

    /// <summary>Creates a new secret. Throws <see cref="InvalidOperationException"/> if the key already exists.</summary>
    Task<Secret> CreateAsync(string key, string value, CancellationToken ct);

    /// <summary>Updates an existing secret's value. Returns false if no secret has that key.</summary>
    Task<bool> UpdateValueAsync(string key, string value, CancellationToken ct);

    /// <summary>Returns false if no secret has that key.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken ct);
}
