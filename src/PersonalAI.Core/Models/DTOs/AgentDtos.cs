using System.Text.Json;
using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.DTOs;

public record AgentTypeDto(AgentType Type, string DisplayName, string Description);

public record AgentDto(
    Guid Id,
    string Name,
    string? Description,
    AgentType Type,
    JsonElement TypeConfig,
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

/// <summary>The body of both creating and editing an agent.</summary>
public record SaveAgentRequest(
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
    string? ReasoningEffort = null,
    // Null creates a General agent. On edit it means "leave the agent's type alone": a client that predates agent
    // types, or one that simply doesn't send them, must not silently turn a Dashboard & Alerts agent into a General one
    // and wipe its settings.
    AgentType? Type = null,
    JsonElement? TypeConfig = null);

public record RunAgentRequest;

/// <summary>A follow-up message to an agent run's conversation.</summary>
public record RunMessageRequest(string? Message);

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
