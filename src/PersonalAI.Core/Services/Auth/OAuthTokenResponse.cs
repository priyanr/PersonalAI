using System.Text.Json.Serialization;

namespace PersonalAI.Core.Services.Auth;

/// <summary>A token endpoint response (RFC 6749 §5.1, plus OIDC's <c>id_token</c>). Only
/// <c>access_token</c> is guaranteed: a refresh response may omit <c>refresh_token</c> (the provider isn't
/// rotating, so the old one stays valid) and <c>expires_in</c> is only RECOMMENDED.</summary>
public class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>Token introspection response (RFC 7662 §2.2): <c>active=false</c> for a revoked/unknown/expired
/// token.</summary>
public class TokenIntrospectionResult
{
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("exp")]
    public long? Exp { get; set; }
}

/// <summary>The provider endpoints actually in use: explicit settings merged over OIDC discovery. The
/// optional ones are null when the provider doesn't offer that feature.</summary>
public record OAuthEndpoints(
    string Authorization,
    string Token,
    string? Revocation,
    string? Introspection,
    string? EndSession);
