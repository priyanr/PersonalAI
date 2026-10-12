using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Interfaces;

public interface IAgentRunsRepository
{
    Task<AgentRun> CreateAsync(Guid agentId, AgentRunTrigger trigger, CancellationToken ct);

    Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct);

    /// <summary>Most recent runs for an agent, newest first.</summary>
    Task<IReadOnlyList<AgentRun>> GetForAgentAsync(Guid agentId, int limit, CancellationToken ct);

    Task CompleteAsync(Guid runId, AgentRunStatus status, string? summary, string transcriptJson, CancellationToken ct);

    /// <summary>Persists transcript progress mid-run (status/summary/completion untouched), so a viewer
    /// polling GetAsync sees the run stream in rather than only appearing once it finishes.</summary>
    Task AppendTranscriptAsync(Guid runId, string transcriptJson, CancellationToken ct);

    /// <summary>Records that the run is about to make another LLM call; returns the run's new count.</summary>
    Task<int> IncrementLlmCallsAsync(Guid runId, CancellationToken ct);

    /// <summary>Adds one provider call's reported usage to the run's totals; returns the new totals.</summary>
    Task<(long InputTokens, long OutputTokens)> AddTokenUsageAsync(Guid runId, AiTokenUsage usage, CancellationToken ct);
}
