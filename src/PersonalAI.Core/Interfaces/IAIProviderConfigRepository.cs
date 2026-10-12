namespace PersonalAI.Core.Interfaces;

public interface IAIProviderConfigRepository
{
    Task<IReadOnlyDictionary<string, string?>> GetValuesAsync(string providerKey, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct);

    /// <summary>Upserts only the field names present in <paramref name="updates"/>; a null value clears that field.</summary>
    Task SetValuesAsync(string providerKey, IReadOnlyDictionary<string, string?> updates, CancellationToken ct);
}
