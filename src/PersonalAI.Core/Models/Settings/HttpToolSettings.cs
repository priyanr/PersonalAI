namespace PersonalAI.Core.Models.Settings;

public class HttpToolSettings
{
    public const string SectionName = "HttpTool";

    /// <summary>Glob patterns ('*' = any run of characters, '?' = any single character) matched
    /// case-insensitively against the full request URL (scheme+host+path+query). A URL that matches
    /// none of these is refused before any request is sent. Empty (the default) permits nothing.</summary>
    public string[] AllowedUrls { get; set; } = [];

    /// <summary>Per-request timeout. A request that doesn't complete in time is cancelled and reported
    /// back to the model as a timeout error.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Response body is truncated to this many characters before being returned to the model,
    /// so one large response can't blow the agent's context window.</summary>
    public int MaxResponseBodyChars { get; set; } = 8000;
}
