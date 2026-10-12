using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.DTOs;

public record AgentDto(
    Guid Id,
    string Name,
    string? Description,
    string Prompt,
    string ProviderKey,
    string ModelName,
    int? NumCtx,
    IReadOnlyList<Guid> EnabledSkillIds,
    IReadOnlyList<string> EnabledTools,
    int? MaxLlmCalls,
    string? ReasoningEffort,
    AgentScheduleKind ScheduleKind,
    DateTimeOffset? ScheduleOnceAt,
    int? IntervalMinutes,
    string? CronExpression,
    bool Enabled,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record AgentScheduleFields(
    AgentScheduleKind ScheduleKind,
    DateTimeOffset? ScheduleOnceAt,
    int? IntervalMinutes,
    string? CronExpression,
    bool Enabled);

public record CreateAgentRequest(
    string Name,
    string? Description,
    string Prompt,
    string? ProviderKey,
    string ModelName,
    int? NumCtx,
    IReadOnlyList<Guid>? EnabledSkillIds,
    IReadOnlyList<string>? EnabledTools,
    int? MaxLlmCalls,
    AgentScheduleKind ScheduleKind,
    DateTimeOffset? ScheduleOnceAt,
    int? IntervalMinutes,
    string? CronExpression,
    bool Enabled,
    string? ReasoningEffort = null);

public record UpdateAgentRequest(
    string Name,
    string? Description,
    string Prompt,
    string? ProviderKey,
    string ModelName,
    int? NumCtx,
    IReadOnlyList<Guid>? EnabledSkillIds,
    IReadOnlyList<string>? EnabledTools,
    int? MaxLlmCalls,
    AgentScheduleKind ScheduleKind,
    DateTimeOffset? ScheduleOnceAt,
    int? IntervalMinutes,
    string? CronExpression,
    bool Enabled,
    string? ReasoningEffort = null);

public record RunAgentRequest;

public record AgentRunSummaryDto(
    Guid Id,
    Guid AgentId,
    AgentRunTrigger Trigger,
    AgentRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Summary,
    int LlmCallCount,
    long TotalInputTokens,
    long TotalOutputTokens);

public record AgentRunDetailDto(
    Guid Id,
    Guid AgentId,
    AgentRunTrigger Trigger,
    AgentRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Summary,
    int LlmCallCount,
    long TotalInputTokens,
    long TotalOutputTokens,
    IReadOnlyList<AgentRunTranscriptEntry> Transcript);

/// <summary>One entry in an AgentRun's recorded transcript: either a chunk of the agent's own assistant
/// text, or a skill it invoked (with the skill's final text result folded into Content).</summary>
public record AgentRunTranscriptEntry(string Kind, string? SkillName, string? Content, bool IsError);
