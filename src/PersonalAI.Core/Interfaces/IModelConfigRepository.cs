namespace PersonalAI.Core.Interfaces;

public interface IModelConfigRepository
{
    /// <summary>Every saved model setting, keyed by provider, then model name, then field name.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>>> GetAllAsync(CancellationToken ct);

    /// <summary>Replaces all of one model's settings with <paramref name="values"/>; blank values are dropped.</summary>
    Task ReplaceAsync(string providerKey, string modelName, IReadOnlyDictionary<string, string?> values, CancellationToken ct);
}
