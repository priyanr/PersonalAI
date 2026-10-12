using System.Collections.Concurrent;
using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Services.Secrets;

/// <summary>In-memory mirror of the Secrets table, kept in sync with the DB by the startup load and by
/// SecretsController after every write, so future consumers (e.g. Stage 3 skills) can read secret values
/// synchronously without a DB round-trip per invocation.</summary>
public sealed class SecretsCache
{
    private readonly ConcurrentDictionary<string, string?> _values = new();

    public string? GetValue(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string? value) => _values[key] = value;

    public void Remove(string key) => _values.TryRemove(key, out _);

    public void LoadAll(IEnumerable<Secret> secrets)
    {
        foreach (var secret in secrets)
            _values[secret.Key] = secret.Value;
    }
}
