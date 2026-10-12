namespace PersonalAI.Core.Services.Settings;

/// <summary>In-memory snapshot of AppSettings, kept in sync with the DB by the startup load and by
/// AppSettingsController after every update, so tools can read it synchronously on the hot path.</summary>
public sealed class AppSettingsCache
{
    public const string DefaultWebSearchGuidance =
        "If two consecutive searches return results that don't mention the topic at all, stop searching " +
        "and answer using your own knowledge or the best result already found — do not retry more than a " +
        "few times with reworded queries.";

    private volatile string? _webSearchGuidance;
    private volatile bool _thinkingEnabled = true;

    public string WebSearchGuidance => _webSearchGuidance ?? DefaultWebSearchGuidance;

    public bool ThinkingEnabled => _thinkingEnabled;

    public void Set(string? webSearchGuidance, bool thinkingEnabled)
    {
        _webSearchGuidance = string.IsNullOrWhiteSpace(webSearchGuidance) ? null : webSearchGuidance;
        _thinkingEnabled = thinkingEnabled;
    }
}
