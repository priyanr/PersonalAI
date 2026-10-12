namespace PersonalAI.Core.Models.Settings;

public class SearchSettings
{
    public const string SectionName = "Search";

    public required string SearxngUrl { get; set; }
    public int MaxResults { get; set; } = 5;
    public bool Enabled { get; set; } = true;

    /// <summary>Comma-separated SearXNG engine shortcuts/names to restrict searches to (e.g. "google,bing").
    /// Null or empty lets SearXNG use whatever engines it has enabled.</summary>
    public string? Engines { get; set; }
}
