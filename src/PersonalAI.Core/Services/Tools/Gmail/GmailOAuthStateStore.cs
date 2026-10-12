using System.Collections.Concurrent;

namespace PersonalAI.Core.Services.Tools.Gmail;

/// <summary>Short-lived CSRF state store for the Gmail OAuth authorize/callback round-trip. Unlike the
/// delegated login flow (where the frontend owns the state check client-side), this callback is a
/// top-level browser redirect straight from Google to this API, so the state must be validated
/// server-side before the authorization code is trusted.</summary>
public sealed class GmailOAuthStateStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTime> _states = new(StringComparer.Ordinal);

    public string Create()
    {
        var state = Guid.NewGuid().ToString("N");
        _states[state] = DateTime.UtcNow.Add(Ttl);
        return state;
    }

    /// <summary>Atomically checks and removes the state; returns false if it never existed or has expired.</summary>
    public bool TryConsume(string state)
    {
        if (!_states.TryRemove(state, out var expiresAtUtc))
            return false;

        return DateTime.UtcNow <= expiresAtUtc;
    }
}
