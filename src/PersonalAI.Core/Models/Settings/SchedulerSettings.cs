namespace PersonalAI.Core.Models.Settings;

public class SchedulerSettings
{
    public const string SectionName = "Scheduler";

    /// <summary>How often the background scheduler polls for due agents.</summary>
    public int PollIntervalSeconds { get; set; } = 30;
}
