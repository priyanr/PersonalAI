using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.Entities;

/// <summary>A prompt-driven orchestrator that invokes Skills and/or directly-enabled Tools (both exposed
/// to its model as tools) and can run unattended on a schedule. Executed by AgentExecutionService through
/// its own short agentic loop, same shape as SkillExecutionService's — every run is recorded as an
/// AgentRun.</summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The agent's system prompt — instructions for when/how to invoke its allowed skills.</summary>
    public required string Prompt { get; set; }

    /// <summary>Which IAIProvider (Key) this agent talks to — "ollama", "openai", "deepseek", etc.</summary>
    public string ProviderKey { get; set; } = "ollama";
    public required string ModelName { get; set; }
    public int? NumCtx { get; set; }

    /// <summary>JSON array of Skill.Id values this agent may invoke as tools — an allowlist, same
    /// rationale as Skill.EnabledToolsJson: unattended execution should only reach what it needs.</summary>
    public string EnabledSkillIdsJson { get; set; } = "[]";

    /// <summary>JSON array of ITool.Name values this agent may call directly (in addition to its
    /// Skills) — the same allowlist shape as Skill.EnabledToolsJson, for tasks that don't need a
    /// reusable Skill wrapper around a single tool.</summary>
    public string EnabledToolsJson { get; set; } = "[]";

    /// <summary>Caps how many LLM calls this agent's run loop may make before it's stopped, overriding
    /// the global Agent:MaxIterations default. Null means "use the global default". The current/max call
    /// count is also relayed to the model itself each call (see AgentExecutionService), so it can pace
    /// itself and wrap up before hitting the cap instead of being cut off mid-task.</summary>
    public int? MaxLlmCalls { get; set; }

    /// <summary>Reasoning effort sent with every LLM call of this agent's runs (one of
    /// ModelSettings.ReasoningEffortLevels). Null uses the model's ReasoningEffort setting on the Models page,
    /// else the model's own default.</summary>
    public string? ReasoningEffort { get; set; }

    public AgentScheduleKind ScheduleKind { get; set; } = AgentScheduleKind.Manual;

    /// <summary>ScheduleKind.Once: the single UTC instant to run at.</summary>
    public DateTimeOffset? ScheduleOnceAt { get; set; }

    /// <summary>ScheduleKind.Interval: minutes between runs.</summary>
    public int? IntervalMinutes { get; set; }

    /// <summary>ScheduleKind.Cron: a standard 5-field UTC cron expression (minute hour day-of-month month day-of-week).</summary>
    public string? CronExpression { get; set; }

    /// <summary>Master on/off switch for the scheduler — a disabled agent can still be run manually and
    /// keeps its NextRunAt, so re-enabling it doesn't silently fire a backlog of missed runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Next UTC instant the scheduler should fire this agent, precomputed on create/update/after
    /// each run so the scheduler's poll loop is a cheap indexed lookup. Null means "never" (Manual, or a
    /// one-off Once schedule that has already fired).</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
