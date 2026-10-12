using System.Text.Json.Serialization;

namespace PersonalAI.Core.Services.Tools.Gmail;

public class GoogleTokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; set; }

    /// <summary>Only present on the FIRST authorization-code exchange — Google omits it on refresh
    /// responses, so callers must never overwrite a previously stored refresh token with a null/empty
    /// value from a refresh response.</summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}
