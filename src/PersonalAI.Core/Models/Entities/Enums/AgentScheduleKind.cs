namespace PersonalAI.Core.Models.Entities.Enums;

public enum AgentScheduleKind
{
    /// <summary>No automatic schedule — the agent only runs when triggered manually from the UI.</summary>
    Manual = 0,
    /// <summary>Runs exactly once, at ScheduleOnceAt.</summary>
    Once = 1,
    /// <summary>Runs every IntervalMinutes, starting from when the schedule was (re)armed.</summary>
    Interval = 2,
    /// <summary>Runs on the recurrence described by a standard 5-field UTC cron expression.</summary>
    Cron = 3
}
