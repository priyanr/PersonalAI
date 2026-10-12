using System.Net.Http.Json;
using System.Text.Json;

namespace PersonalAI.Core.Services.Tools.Gmail;

/// <summary>Talks to Google's OAuth2 token endpoint for the Gmail tool. Unlike the login OAuthClient, the
/// Client ID/Secret are not app-level config — they're user-supplied per deployment (self-hosted
/// instances each register their own redirect URI with Google), so they're read per-call from
/// ToolConfigCache rather than IOptions.</summary>
public class GmailOAuthClient(HttpClient httpClient, ToolConfigCache configCache)
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    public async Task<GoogleTokenResponse> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct)
    {
        var (clientId, clientSecret) = GetClientCredentials();

        var response = await httpClient.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            }),
            ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google token exchange failed: {await DescribeErrorAsync(response, ct)}");

        return (await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct))!;
    }

    /// <summary>Returns null on a transient failure; throws GmailOAuthInvalidGrantException when Google
    /// reports the refresh token itself is no longer valid (revoked/expired) so the caller can
    /// distinguish "try again later" from "the user must reconnect".</summary>
    public async Task<GoogleTokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var (clientId, clientSecret) = GetClientCredentials();

        var response = await httpClient.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            }),
            ct);

        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken: ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (TryGetErrorCode(body) == "invalid_grant")
            throw new GmailOAuthInvalidGrantException(body);

        return null;
    }

    private (string ClientId, string ClientSecret) GetClientCredentials()
    {
        var clientId = configCache.GetValue("gmail", "ClientId");
        var clientSecret = configCache.GetValue("gmail", "ClientSecret");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            throw new InvalidOperationException("Gmail OAuth Client ID/Secret are not configured.");
        return (clientId, clientSecret);
    }

    private static async Task<string> DescribeErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var code = TryGetErrorCode(body);
        return code is not null ? $"{(int)response.StatusCode} {code}" : $"{(int)response.StatusCode} {body}";
    }

    private static string? TryGetErrorCode(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            return doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                ? err.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
