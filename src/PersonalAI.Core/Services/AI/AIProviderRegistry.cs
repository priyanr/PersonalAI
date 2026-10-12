using PersonalAI.Core.Interfaces;

namespace PersonalAI.Core.Services.AI;

public sealed class AIProviderRegistry(IEnumerable<IAIProvider> providers)
{
    private readonly IReadOnlyDictionary<string, IAIProvider> _byKey =
        providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IAIProvider> All => [.. _byKey.Values];

    public IAIProvider Resolve(string providerKey) =>
        _byKey.TryGetValue(providerKey, out var provider)
            ? provider
            : throw new InvalidOperationException($"Unknown AI provider '{providerKey}'.");

    public bool TryResolve(string providerKey, out IAIProvider? provider) =>
        _byKey.TryGetValue(providerKey, out provider);
}
