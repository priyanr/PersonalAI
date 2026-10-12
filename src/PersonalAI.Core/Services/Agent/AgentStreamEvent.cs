namespace PersonalAI.Core.Services.Agent;

public abstract record AgentStreamEvent;

public sealed record TextDeltaEvent(string Text) : AgentStreamEvent;

/// <summary>A fragment of the model's thinking, streamed so a long silent reasoning phase is visible. Live
/// only: never persisted, never sent back to the model, and not kept in a chat turn's replay buffer.</summary>
public sealed record ReasoningDeltaEvent(string Text) : AgentStreamEvent;

/// <summary>Emitted once, immediately, when an AgentExecutionService run starts — lets a live SSE viewer
/// link straight to this run's history entry without waiting for it to finish.</summary>
public sealed record AgentRunStartedEvent(Guid RunId) : AgentStreamEvent;

public sealed record ToolResultEvent(Guid ToolInvocationId, string ToolName, string? ResultContent, bool IsError) : AgentStreamEvent;

public sealed record ApprovalRequiredEvent(Guid ToolInvocationId, string ToolName, string ResolvedPath, string Reason) : AgentStreamEvent;

public sealed record DoneEvent : AgentStreamEvent;

public sealed record ErrorEvent(string Message) : AgentStreamEvent;

/// <summary>Emitted just before each provider call so a live viewer can show the count as it climbs instead
/// of only after the turn/run ends. <paramref name="TotalCalls"/> is the persisted count after this call was
/// recorded: the session's lifetime total for chat, the run's own count for an agent run.</summary>
public sealed record LlmCallEvent(int TotalCalls) : AgentStreamEvent;

/// <summary>Emitted after a chat provider call whose provider reported token usage, carrying the session's new
/// lifetime totals (which also include any history-compaction calls made since the last event).</summary>
public sealed record TokenUsageEvent(long TotalInputTokens, long TotalOutputTokens) : AgentStreamEvent;

public sealed record ToolProgressEvent(Guid ToolInvocationId, string ToolName, string Message) : AgentStreamEvent;

/// <summary>Emitted first by every chat turn (a new message or a resumed approval). Everything in the session
/// with a SequenceNumber above <paramref name="AfterSequenceNumber"/> belongs to this turn, so a client that
/// reloads mid-turn and replays the buffered events can hide those persisted messages until the turn ends
/// instead of rendering them twice (once from the DB, once from the replayed stream).</summary>
public sealed record TurnStartedEvent(int AfterSequenceNumber) : AgentStreamEvent;

/// <summary>Emitted by a manual compaction once the conversation so far has been folded into the session's
/// history summary. <paramref name="FoldedMessages"/> is 0 when there was nothing left to summarize.</summary>
public sealed record CompactionCompletedEvent(int FoldedMessages) : AgentStreamEvent;
