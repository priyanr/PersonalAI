using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.Entities;

/// <summary>One recorded execution of an Agent — either fired by the scheduler or triggered manually
/// from the UI. TranscriptJson holds a condensed, replayable record of the run (assistant text and each
/// skill invocation) for the run history UI.</summary>
public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required Guid AgentId { get; set; }

    public AgentRunTrigger Trigger { get; set; } = AgentRunTrigger.Manual;
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Running;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The final assistant text on success, or the error message on failure.</summary>
    public string? Summary { get; set; }

    /// <summary>How many LLM calls this run has made so far (updated live as the run progresses).</summary>
    public int LlmCallCount { get; set; }

    /// <summary>Prompt tokens across this run's own provider calls (not nested skill runs), as reported by
    /// the provider. Calls whose provider reports no usage add nothing.</summary>
    public long TotalInputTokens { get; set; }

    /// <summary>Completion tokens, same basis as <see cref="TotalInputTokens"/>.</summary>
    public long TotalOutputTokens { get; set; }

    /// <summary>JSON array of AgentRunTranscriptEntry (Models/DTOs/AgentDtos.cs).</summary>
    public string TranscriptJson { get; set; } = "[]";
}
