namespace PersonalAI.Core.Models.Settings;

public class AgentSettings
{
    public const string SectionName = "Agent";

    public int MaxIterations { get; set; } = 25;
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>When a session has TrimHistoryEnabled, tool-result message content longer than this is truncated before being sent to Ollama.</summary>
    public int ToolResultMaxChars { get; set; } = 4000;

    /// <summary>When a session has TrimHistoryEnabled, only the most recent N history messages are sent to Ollama raw; older ones are folded into a running summary (see COMPACTION_ARCHITECTURE.md) instead of being dropped.</summary>
    public int HistoryKeepRecentMessages { get; set; } = 40;

    /// <summary>A compaction pass runs once this many messages older than the keep window have accumulated without being folded into the summary yet — whichever of this or CompactionMaxCharsPerCall is reached first.</summary>
    public int CompactionBatchSize { get; set; } = 20;

    /// <summary>Also triggers a compaction pass once the not-yet-folded messages' total (already-capped) character count reaches this — protects against a handful of large tool outputs (e.g. sandbox exec) inflating the wire payload before CompactionBatchSize's message count is reached. Also bounds how much a single compaction call folds in at once, so a large first-time backlog catches up over several turns instead of one huge call.</summary>
    public int CompactionMaxCharsPerCall { get; set; } = 24000;

    /// <summary>Hard cap on ChatSession.HistorySummary's length. Every compaction pass threads the whole
    /// previous summary back into the next summarization prompt with no shrinkage of its own, so without
    /// a cap an imperfectly-compressing model could let the summary itself grow unboundedly over a very
    /// long-lived session. Exceeding this truncates the newest summary (oldest bullet points first).</summary>
    public int CompactionMaxSummaryChars { get; set; } = 8000;

    /// <summary>How many times an unattended Agent run retries a model call that failed with a transient network error before anything was received.</summary>
    public int ProviderRetryCount { get; set; } = 3;

    /// <summary>Delay between those retries.</summary>
    public int ProviderRetryDelaySeconds { get; set; } = 10;
}
