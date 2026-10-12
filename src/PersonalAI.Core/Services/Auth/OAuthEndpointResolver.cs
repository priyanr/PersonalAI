using System.Text.Json;
using Microsoft.Extensions.Options;
using PersonalAI.Core.Models.Settings;

namespace PersonalAI.Core.Services.Auth;

/// <summary>Works out which provider endpoints to call: each explicit <see cref="AuthSettings"/> endpoint
/// wins, and the rest come from the OIDC discovery document when <see cref="AuthSettings.UseDiscovery"/> is
/// on. The discovery document is fetched lazily, cached for <see cref="CacheDuration"/>, and a failed fetch
/// is not cached (it throws <see cref="HttpRequestException"/> so callers can answer 503 and retry later).
/// Singleton; the <see cref="HttpClient"/> factory is called per fetch so DNS changes are picked up.</summary>
public class OAuthEndpointResolver(Func<HttpClient> httpClientFactory, IOptions<AuthSettings> authSettings, TimeProvider clock)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthEndpoints? _cached;
    private DateTimeOffset _cachedUntil;

    private AuthSettings Settings => authSettings.Value;

    public async Task<OAuthEndpoints> GetAsync(CancellationToken ct)
    {
        if (_cached is not null && clock.GetUtcNow() < _cachedUntil)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && clock.GetUtcNow() < _cachedUntil)
                return _cached;

            JsonElement? discovered = Settings.UseDiscovery ? await FetchDiscoveryAsync(ct) : null;
            var endpoints = new OAuthEndpoints(
                Authorization: Pick(Settings.AuthorizationEndpoint, discovered, "authorization_endpoint")
                    ?? throw new InvalidOperationException("No OAuth2 authorization endpoint: set Auth:AuthorizationEndpoint or enable discovery."),
                Token: Pick(Settings.TokenEndpoint, discovered, "token_endpoint")
                    ?? throw new InvalidOperationException("No OAuth2 token endpoint: set Auth:TokenEndpoint or enable discovery."),
                Revocation: Pick(Settings.RevocationEndpoint, discovered, "revocation_endpoint"),
                Introspection: Pick(Settings.IntrospectionEndpoint, discovered, "introspection_endpoint"),
                EndSession: Pick(Settings.EndSessionEndpoint, discovered, "end_session_endpoint"));

            _cached = endpoints;
            _cachedUntil = clock.GetUtcNow() + CacheDuration;
            return endpoints;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Pick(string configured, JsonElement? discovered, string discoveryField)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Settings.ResolveEndpoint(configured);
        if (discovered is { } doc
            && doc.TryGetProperty(discoveryField, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString();
        return null;
    }

    private async Task<JsonElement> FetchDiscoveryAsync(CancellationToken ct)
    {
        var address = Settings.ResolveMetadataAddress();
        if (Settings.RequireHttpsMetadata && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"OIDC discovery address '{address}' is not https; set Auth:RequireHttpsMetadata=false to allow it.");

        using var response = await httpClientFactory().GetAsync(address, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // Treated like an unreachable provider: callers answer 503 and the next call retries.
            throw new HttpRequestException($"OIDC discovery document at '{address}' is not valid JSON.", ex);
        }
    }
}
