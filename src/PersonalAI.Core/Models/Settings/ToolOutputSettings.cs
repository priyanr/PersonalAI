namespace PersonalAI.Core.Models.Settings;

/// <summary>Root directory for files a tool produces as a side effect (e.g. computer_control
/// screenshots) rather than at the user's request into their own workspace. Kept separate from
/// WorkspaceSettings.RootPath so this output never shows up in ListFiles/SearchFiles results or
/// otherwise mixes with the user's actual workspace sandbox.</summary>
public class ToolOutputSettings
{
    public const string SectionName = "ToolOutput";

    public required string RootPath { get; set; }
}
