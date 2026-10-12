using System.Net.Http.Headers;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Services.Tools.Gmail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace PersonalAI.Api.Controllers;

/// <summary>Handles the Google OAuth2 Authorization Code round-trip for the Gmail tool. Unlike
/// AuthController (which delegates the CSRF `state` check to the frontend), the callback here is a
/// top-level browser redirect straight from Google to this API, so `state` must be validated
/// server-side (GmailOAuthStateStore) before the authorization code is trusted.</summary>
[ApiController]
[Route("api/tools/gmail/oauth")]
public class GmailOAuthController(
    GmailOAuthClient oauthClient,
    GmailOAuthStateStore stateStore,
    ToolConfigCache configCache,
    IToolConfigRepository configRepo,
    IHttpClientFactory httpClientFactory,
    IOptions<GmailSettings> gmailSettings) : ControllerBase
{
    // gmail.readonly can't change labels, so mark_read (messages.modify) got 403 ACCESS_TOKEN_SCOPE_INSUFFICIENT.
    // gmail.modify is the narrowest scope covering both read and label changes; it still excludes sending mail
    // and permanent deletion, matching GmailTool's own "never send or delete" contract.
    private const string GmailScope = "https://www.googleapis.com/auth/gmail.modify";
    private const string GoogleAuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleUserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";

    [HttpGet("redirect-uri")]
    public ActionResult<object> RedirectUri() => Ok(new { redirectUri = BuildRedirectUri() });

    [HttpGet("authorize-url")]
    public ActionResult<object> AuthorizeUrl()
    {
        var clientId = configCache.GetValue("gmail", "ClientId");
        if (string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { message = "Gmail OAuth Client ID is not configured. Set it in Settings > Tools first." });

        var state = stateStore.Create();
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = BuildRedirectUri(),
            ["response_type"] = "code",
            ["scope"] = GmailScope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state
        };

        return Ok(new { url = GoogleAuthorizeEndpoint + ToQueryString(query) });
    }

    /// <summary>Google redirects the browser here directly (no Authorization header), so this must stay
    /// reachable even when global auth is enabled — matching how AuthController's own bootstrap actions
    /// (Login/Exchange/Refresh/Logout) are unconditionally [AllowAnonymous] regardless of whether the
    /// AuthorizeFilter is registered.</summary>
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? code, string? state, CancellationToken ct)
    {
        var frontendBase = gmailSettings.Value.FrontendBaseUrl.TrimEnd('/');
        var settingsUrl = $"{frontendBase}/settings/tools?connected=gmail";

        if (string.IsNullOrEmpty(state) || !stateStore.TryConsume(state))
            return Redirect($"{settingsUrl}&error=invalid_state");

        if (string.IsNullOrEmpty(code))
            return Redirect($"{settingsUrl}&error=missing_code");

        try
        {
            var tokens = await oauthClient.ExchangeCodeAsync(code, BuildRedirectUri(), ct);

            if (string.IsNullOrEmpty(tokens.RefreshToken))
                return Redirect($"{settingsUrl}&error=no_refresh_token");

            var email = await TryGetConnectedEmailAsync(tokens.AccessToken, ct);

            var updates = new Dictionary<string, string?>
            {
                ["AccessToken"] = tokens.AccessToken,
                ["RefreshToken"] = tokens.RefreshToken,
                ["TokenExpiresAtUtc"] = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn).ToString("o"),
                ["ConnectedEmail"] = email
            };
            await configRepo.SetValuesAsync("gmail", updates, ct);
            configCache.MergeTool("gmail", updates);

            return Redirect(settingsUrl);
        }
        catch (Exception)
        {
            // Top-level browser navigation — never throw/return JSON here, just bounce back with a short reason.
            return Redirect($"{settingsUrl}&error=exchange_failed");
        }
    }

    private async Task<string?> TryGetConnectedEmailAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, GoogleUserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string BuildRedirectUri() => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/tools/gmail/oauth/callback";

    private static string ToQueryString(Dictionary<string, string> query) =>
        "?" + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
}
