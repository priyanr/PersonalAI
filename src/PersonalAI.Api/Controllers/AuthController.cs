using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Auth;

namespace PersonalAI.Api.Controllers;

/// <summary>Pure REST/bearer-token auth: every action here either issues a token pair as JSON or
/// reads the standard Authorization header JwtBearer already parsed — never a cookie. The frontend
/// owns the CSRF `state` check and the PKCE verifier (it generates both itself and keeps them until the
/// provider redirects back), so this controller doesn't need to persist any state server-side.
///
/// Provider trouble (unreachable, 5xx, broken discovery) is answered with 503 everywhere, never 401: a
/// 401 tells the frontend the session is over, which a provider outage doesn't mean.</summary>
[ApiController]
[Route("api/auth")]
public class AuthController(OAuthClient oauthClient, IOptions<AuthSettings> authSettings) : ControllerBase
{
    private const int FallbackExpiresInSeconds = 300;
    private static readonly JsonWebTokenHandler TokenHandler = new();

    private AuthSettings Settings => authSettings.Value;

    [AllowAnonymous]
    [HttpGet("status")]
    public ActionResult<AuthStatusDto> Status()
    {
        return Ok(new AuthStatusDto(Settings.Enabled));
    }

    /// <param name="code_challenge">Optional PKCE S256 challenge (RFC 7636) computed by the frontend.</param>
    [AllowAnonymous]
    [HttpGet("login")]
    public async Task<IActionResult> Login(string state, [FromQuery(Name = "code_challenge")] string? codeChallenge, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state))
            return BadRequest("state is required");

        try
        {
            return Redirect(await oauthClient.BuildAuthorizeUrlAsync(state, codeChallenge, ct));
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The login provider is unavailable.");
        }
    }

    [AllowAnonymous]
    [HttpPost("exchange")]
    public async Task<ActionResult<TokenResponseDto>> Exchange(ExchangeCodeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Code))
            return BadRequest("code is required");

        OAuthTokenResponse? tokens;
        try
        {
            tokens = await oauthClient.ExchangeCodeAsync(request.Code, request.CodeVerifier, ct);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (tokens is null)
            return Unauthorized();

        return Ok(ToDto(tokens));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<TokenResponseDto>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return BadRequest("refreshToken is required");

        OAuthTokenResponse? tokens;
        try
        {
            tokens = await oauthClient.RefreshAsync(request.RefreshToken, ct);
        }
        catch (HttpRequestException)
        {
            // Provider unreachable or erroring: 503 tells the frontend to keep its tokens and try again
            // later, whereas 401 means the refresh token was rejected and the user must log in again.
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (tokens is null)
            return Unauthorized();

        return Ok(ToDto(tokens));
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<ActionResult<LogoutResponseDto>> Logout(LogoutRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.RefreshToken))
            await oauthClient.RevokeAsync(request.RefreshToken, "refresh_token", ct);

        string? endSessionUrl = null;
        try
        {
            endSessionUrl = await oauthClient.BuildEndSessionUrlAsync(request.IdToken, ct);
        }
        catch (HttpRequestException)
        {
            // Local logout still happens; the provider's own session just isn't ended.
        }
        return Ok(new LogoutResponseDto(endSessionUrl));
    }

    [HttpGet("me")]
    public ActionResult<MeDto> Me()
    {
        if (!Settings.Enabled)
            return Ok(new MeDto(true, "local", ""));

        var subject = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "";
        return Ok(new MeDto(true, subject, ScopeOf(User.Claims)));
    }

    /// <summary>Scopes live in <c>scope</c> (RFC 9068, space-separated) or <c>scp</c> (Entra: space-separated
    /// string; Okta: a JSON array, which arrives as one claim per entry).</summary>
    private static string ScopeOf(IEnumerable<Claim> claims)
    {
        return string.Join(' ', claims.Where(c => c.Type is "scope" or "scp").Select(c => c.Value));
    }

    private static TokenResponseDto ToDto(OAuthTokenResponse tokens)
    {
        // Access tokens aren't guaranteed to be readable JWTs (they're opaque to clients by spec), so claims
        // come from whichever of access/id token parses, and the response's own fields win when present.
        var accessJwt = TryRead(tokens.AccessToken);
        var idJwt = TryRead(tokens.IdToken);

        var subject = accessJwt?.Subject ?? idJwt?.Subject ?? "";
        var scope = tokens.Scope ?? (accessJwt is null ? "" : ScopeOf(accessJwt.Claims));

        var expiresIn = tokens.ExpiresIn is > 0 ? tokens.ExpiresIn.Value
            : accessJwt is not null && accessJwt.ValidTo > DateTime.UtcNow ? (int)(accessJwt.ValidTo - DateTime.UtcNow).TotalSeconds
            : FallbackExpiresInSeconds;

        return new TokenResponseDto(tokens.AccessToken, tokens.RefreshToken ?? "", expiresIn, subject, scope, tokens.IdToken);
    }

    private static JsonWebToken? TryRead(string? token)
    {
        if (string.IsNullOrEmpty(token) || !TokenHandler.CanReadToken(token))
            return null;
        try
        {
            return TokenHandler.ReadJsonWebToken(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
