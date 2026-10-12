namespace PersonalAI.Core.Interfaces;

public interface IToolConfigRepository
{
    Task<IReadOnlyDictionary<string, string?>> GetValuesAsync(string toolName, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct);

    /// <summary>Upserts only the field names present in <paramref name="updates"/>; a null value clears that field.</summary>
    Task SetValuesAsync(string toolName, IReadOnlyDictionary<string, string?> updates, CancellationToken ct);
}
