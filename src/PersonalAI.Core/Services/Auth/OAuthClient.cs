using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using PersonalAI.Core.Models.Settings;

namespace PersonalAI.Core.Services.Auth;

/// <summary>Talks to the external OAuth2 / OpenID Connect provider PersonalAI delegates login to, using only
/// standard endpoints: builds the Authorization Code (+ PKCE) redirect URL, exchanges and refreshes tokens
/// (RFC 6749), revokes them (RFC 7009), introspects them (RFC 7662), and builds the RP-initiated logout URL
/// (OIDC). Which URLs those are comes from <see cref="OAuthEndpointResolver"/>.
///
/// Error contract: a 4xx from the provider means the grant/token itself was rejected and is reported as
/// null; anything else (5xx, unreachable, bad discovery) throws <see cref="HttpRequestException"/> so the
/// API can answer 503 instead of logging the user out over a provider outage.</summary>
public class OAuthClient(HttpClient httpClient, OAuthEndpointResolver endpointResolver, IOptions<AuthSettings> authSettings)
{
    private AuthSettings Settings => authSettings.Value;

    private bool UseBasicAuth =>
        string.Equals(Settings.TokenEndpointAuthMethod, "client_secret_basic", StringComparison.OrdinalIgnoreCase);

    public async Task<string> BuildAuthorizeUrlAsync(string state, string? codeChallenge, CancellationToken ct)
    {
        var endpoints = await endpointResolver.GetAsync(ct);
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = Settings.ClientId,
            ["redirect_uri"] = Settings.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = Settings.Scope,
            ["state"] = state
        };
        if (!string.IsNullOrEmpty(codeChallenge))
        {
            query["code_challenge"] = codeChallenge;
            query["code_challenge_method"] = "S256";
        }
        foreach (var (key, value) in Settings.AuthorizeParameters)
            query.TryAdd(key, value);

        return AppendQuery(endpoints.Authorization, query);
    }

    /// <summary>Null when the provider rejects the code (expired, reused, wrong verifier).</summary>
    public async Task<OAuthTokenResponse?> ExchangeCodeAsync(string code, string? codeVerifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Settings.RedirectUri
        };
        if (!string.IsNullOrEmpty(codeVerifier))
            form["code_verifier"] = codeVerifier;

        return await PostTokenRequestAsync(form, ct);
    }

    /// <summary>Null when the refresh token is rejected. If the provider doesn't rotate refresh tokens it may
    /// leave <c>refresh_token</c> out of the response; the one sent is then still valid and is carried over.</summary>
    public async Task<OAuthTokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };
        if (!string.IsNullOrEmpty(Settings.Scope))
            form["scope"] = Settings.Scope;

        var tokens = await PostTokenRequestAsync(form, ct);
        if (tokens is not null && string.IsNullOrEmpty(tokens.RefreshToken))
            tokens.RefreshToken = refreshToken;
        return tokens;
    }

    /// <summary>Asks the provider whether an access token is still active (RFC 7662). Returns null when the
    /// provider has no introspection endpoint, i.e. revocation can't be checked. Throws
    /// <see cref="HttpRequestException"/> if the provider can't be reached or errors.</summary>
    public async Task<TokenIntrospectionResult?> IntrospectAsync(string accessToken, CancellationToken ct)
    {
        var endpoints = await endpointResolver.GetAsync(ct);
        if (endpoints.Introspection is null)
            return null;

        using var request = BuildClientAuthenticatedRequest(endpoints.Introspection, new Dictionary<string, string>
        {
            ["token"] = accessToken,
            ["token_type_hint"] = "access_token"
        });
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenIntrospectionResult>(ct)) ?? new TokenIntrospectionResult();
    }

    /// <summary>Best-effort RFC 7009 revocation; a no-op when the provider has no revocation endpoint.</summary>
    public async Task RevokeAsync(string token, string tokenTypeHint, CancellationToken ct)
    {
        try
        {
            var endpoints = await endpointResolver.GetAsync(ct);
            if (endpoints.Revocation is null)
                return;

            using var request = BuildClientAuthenticatedRequest(endpoints.Revocation, new Dictionary<string, string>
            {
                ["token"] = token,
                ["token_type_hint"] = tokenTypeHint
            });
            using var _ = await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            // Best-effort: the frontend has already dropped its tokens regardless of whether the provider is reachable.
        }
    }

    /// <summary>OIDC RP-initiated logout URL, so the provider's own session ends too (otherwise the next
    /// login silently signs the same user back in). Null when the provider has no end-session endpoint.</summary>
    public async Task<string?> BuildEndSessionUrlAsync(string? idTokenHint, CancellationToken ct)
    {
        var endpoints = await endpointResolver.GetAsync(ct);
        if (endpoints.EndSession is null)
            return null;

        return AppendQuery(endpoints.EndSession, new Dictionary<string, string?>
        {
            ["client_id"] = Settings.ClientId,
            ["id_token_hint"] = idTokenHint,
            ["post_logout_redirect_uri"] = Settings.PostLogoutRedirectUri
        });
    }

    private async Task<OAuthTokenResponse?> PostTokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var endpoints = await endpointResolver.GetAsync(ct);
        using var request = BuildClientAuthenticatedRequest(endpoints.Token, form);
        using var response = await httpClient.SendAsync(request, ct);

        // invalid_grant / invalid_client etc. come back as 400 (or 401 for client auth failures).
        if ((int)response.StatusCode is >= 400 and < 500)
            return null;
        response.EnsureSuccessStatusCode();

        var tokens = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(ct);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            throw new HttpRequestException("Token endpoint response had no access_token.");
        return tokens;
    }

    private HttpRequestMessage BuildClientAuthenticatedRequest(string url, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        var hasSecret = !string.IsNullOrEmpty(Settings.ClientSecret);

        if (hasSecret && UseBasicAuth)
        {
            // RFC 6749 §2.3.1: both parts are form-urlencoded before being joined and base64'd.
            var credentials = $"{Uri.EscapeDataString(Settings.ClientId)}:{Uri.EscapeDataString(Settings.ClientSecret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }
        else
        {
            form["client_id"] = Settings.ClientId;
            if (hasSecret)
                form["client_secret"] = Settings.ClientSecret;
        }

        request.Content = new FormUrlEncodedContent(form);
        return request;
    }

    private static string AppendQuery(string url, Dictionary<string, string?> query)
    {
        var parts = query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        var separator = url.Contains('?') ? "&" : "?";
        return url + separator + string.Join("&", parts);
    }
}
