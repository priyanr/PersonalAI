using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PersonalAI.Core.Services.Auth;

/// <summary>Remembers, in memory, the result of the provider's token introspection (RFC 7662) per access token so
/// the provider is asked at most once per <c>validity</c> window (default one minute) no matter how many
/// API calls carry that token. Keyed by a hash of the token, never the token itself. Provider errors are
/// not cached and propagate to the caller. A provider without an introspection endpoint can't report
/// revocation, so every token counts as active (the JWT's own signature and lifetime checks still apply).</summary>
public class TokenValidationCache(Func<OAuthClient> clientFactory, TimeSpan validity, TimeProvider clock)
{
    private sealed record Entry(bool Active, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<bool> IsActiveAsync(string token, CancellationToken ct)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        if (TryGetFresh(key, out var cached))
            return cached;

        // One provider call per token even when several requests arrive at once.
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetFresh(key, out cached))
                return cached;

            var result = await clientFactory().IntrospectAsync(token, ct) ?? new TokenIntrospectionResult { Active = true };
            var now = clock.GetUtcNow();
            var expiresAt = now + validity;
            if (result.Exp is { } exp && DateTimeOffset.FromUnixTimeSeconds(exp) is var tokenExp && tokenExp < expiresAt)
                expiresAt = tokenExp;

            Sweep(now);
            _entries[key] = new Entry(result.Active, expiresAt);
            return result.Active;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(string key, out bool active)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > clock.GetUtcNow())
        {
            active = entry.Active;
            return true;
        }
        active = false;
        return false;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now && _entries.TryRemove(key, out _))
                _locks.TryRemove(key, out _);
        }
    }
}
