namespace PersonalAI.Core.Models.DTOs;

public record AuthStatusDto(bool Enabled);

public record MeDto(bool Authenticated, string Subject, string Scope);

/// <summary><paramref name="CodeVerifier"/> is the PKCE verifier whose S256 challenge the frontend sent to
/// /api/auth/login; null when the browser couldn't compute one (no WebCrypto on a plain-http origin).</summary>
public record ExchangeCodeRequest(string Code, string? CodeVerifier = null);

public record RefreshRequest(string RefreshToken);

/// <summary><paramref name="IdToken"/> is passed to the provider's end-session endpoint as <c>id_token_hint</c>.</summary>
public record LogoutRequest(string? RefreshToken, string? IdToken = null);

/// <summary><paramref name="EndSessionUrl"/>: where the browser should go to end the provider's own session
/// (OIDC RP-initiated logout); null when the provider has no end-session endpoint.</summary>
public record LogoutResponseDto(string? EndSessionUrl);

public record TokenResponseDto(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    string Subject,
    string Scope,
    string? IdToken = null);
