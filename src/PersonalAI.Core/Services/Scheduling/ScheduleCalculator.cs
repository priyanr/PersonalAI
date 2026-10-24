using PersonalAI.Core.Models.Entities.Enums;
// Alias needed: PersonalAI.Core.Services.Agent (the ChatAgentService namespace) is a sibling of this
// file's namespace, so the unqualified type name "Agent" resolves to that namespace instead of the
// Models.Entities.Agent entity type — alias to a non-colliding name instead.
using AgentEntity = PersonalAI.Core.Models.Entities.Agent;

namespace PersonalAI.Core.Services.Scheduling;

/// <summary>Computes an Agent's next scheduled run and validates the schedule fields required for
/// its ScheduleKind. Kept separate from the Agent entity/repository so the pure scheduling logic
/// (and CronExpression) can be unit-tested without a database.</summary>
public static class ScheduleCalculator
{
    /// <summary>Returns an error message if the agent's schedule fields are inconsistent with its
    /// ScheduleKind, or null if they're valid.</summary>
    public static string? Validate(AgentEntity agent)
    {
        return agent.ScheduleKind switch
        {
            AgentScheduleKind.Manual => null,
            AgentScheduleKind.Once when agent.ScheduleOnceAt is null => "ScheduleOnceAt is required for a Once schedule.",
            AgentScheduleKind.Once => null,
            AgentScheduleKind.Interval when agent.IntervalMinutes is null or < 1 => "IntervalMinutes must be at least 1 for an Interval schedule.",
            AgentScheduleKind.Interval => null,
            AgentScheduleKind.Cron when string.IsNullOrWhiteSpace(agent.CronExpression) => "CronExpression is required for a Cron schedule.",
            AgentScheduleKind.Cron => ValidateCron(agent.CronExpression!),
            _ => "Unknown schedule kind."
        };
    }

    private static string? ValidateCron(string expression)
    {
        try
        {
            CronExpression.Parse(expression);
            return null;
        }
        catch (FormatException ex)
        {
            return $"Invalid cron expression: {ex.Message}";
        }
    }

    /// <summary>Computes the next UTC instant this agent should run, strictly after <paramref name="fromUtc"/>.
    /// Returns null when the agent has no future run (Manual, or a Once schedule whose instant has already
    /// passed / already ran).</summary>
    public static DateTimeOffset? ComputeNextRunAt(AgentEntity agent, DateTimeOffset fromUtc)
    {
        if (!agent.Enabled) return null;

        return agent.ScheduleKind switch
        {
            AgentScheduleKind.Manual => null,
            AgentScheduleKind.Once => agent.LastRunAt is null && agent.ScheduleOnceAt is { } at && at > fromUtc ? at : null,
            AgentScheduleKind.Interval => agent.IntervalMinutes is { } minutes and >= 1 ? fromUtc.AddMinutes(minutes) : null,
            AgentScheduleKind.Cron => TryGetNextCronOccurrence(agent.CronExpression, fromUtc),
            _ => null
        };
    }

    private static DateTimeOffset? TryGetNextCronOccurrence(string? expression, DateTimeOffset fromUtc)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        try
        {
            return CronExpression.Parse(expression).GetNextOccurrence(fromUtc);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
