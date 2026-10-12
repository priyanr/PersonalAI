using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Core.Interfaces;

public interface IChatAgentService
{
    /// <summary>Persists the user message and runs the agent loop until it produces a final answer,
    /// hits the iteration cap, or pauses on a pending tool-approval.</summary>
    IAsyncEnumerable<AgentStreamEvent> RunTurnAsync(Guid sessionId, string userText, CancellationToken ct);

    /// <summary>Resumes a turn after all pending approvals for it have been resolved.</summary>
    IAsyncEnumerable<AgentStreamEvent> ContinueTurnAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Approves or denies a pending tool invocation, then continues the turn once no
    /// pending approvals remain for its requesting message.</summary>
    IAsyncEnumerable<AgentStreamEvent> ResolveApprovalAsync(Guid toolInvocationId, bool approved, CancellationToken ct);

    /// <summary>Folds everything not yet summarized — including the recent kept window — into the session's
    /// history summary, so the next turn sends the model the summary plus only newer messages. The raw
    /// messages stay in the transcript. See COMPACTION_ARCHITECTURE.md.</summary>
    IAsyncEnumerable<AgentStreamEvent> CompactHistoryAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Assistant text streamed during the current LLM call but not yet persisted as a message —
    /// what a turn interrupted mid-stream would otherwise lose. Null between calls.</summary>
    string? InFlightAssistantText { get; }

    /// <summary>Brings a turn that ended abnormally (stopped, failed, server shutdown) back to a consistent
    /// transcript: every tool call of the latest Assistant tool-call message that has neither a result
    /// nor a pending approval gets an "interrupted" result, <paramref name="partialAssistantText"/> is
    /// persisted as an Assistant message, and <paramref name="systemNotice"/> (if any) is appended as a
    /// System message. Meant to run on a fresh DI scope — the interrupted one's DbContext may hold
    /// half-saved entities from the cancelled SaveChanges.</summary>
    Task FinalizeInterruptedTurnAsync(Guid sessionId, string? partialAssistantText, string interruptionReason, string? systemNotice, CancellationToken ct);
}
