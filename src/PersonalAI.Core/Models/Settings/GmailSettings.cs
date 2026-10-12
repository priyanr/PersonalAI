namespace PersonalAI.Core.Models.Settings;

/// <summary>Deployment-level settings for the Gmail tool's OAuth callback redirect. The Google OAuth
/// Client ID/Secret themselves are user-supplied per-deployment via ToolConfig (Settings > Tools),
/// not here — self-hosted instances each have their own redirect URI registered with Google, so there
/// is no bundled app-wide client.</summary>
public class GmailSettings
{
    public const string SectionName = "Gmail";

    /// <summary>Base URL of the frontend (e.g. http://localhost:3000, or the deployment's real origin)
    /// that the OAuth callback redirects back to after completing/failing the connection. Should match
    /// the frontend's WEB_PORT/deployment URL.</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:3000";
}
