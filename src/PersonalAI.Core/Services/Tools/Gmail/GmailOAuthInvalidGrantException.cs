namespace PersonalAI.Core.Services.Tools.Gmail;

/// <summary>Thrown by GmailOAuthClient.RefreshAsync when Google responds with error "invalid_grant" —
/// meaning the refresh token was revoked or expired, as distinct from a transient failure. GmailTool
/// uses this to know it should clear the stored connection rather than just report a temporary error.</summary>
public sealed class GmailOAuthInvalidGrantException : Exception
{
    public GmailOAuthInvalidGrantException(string? message) : base(message ?? "invalid_grant") { }
}
