namespace PersonalAI.Core.Models.Settings;

public class WorkspaceSettings
{
    public const string SectionName = "Workspace";

    public required string RootPath { get; set; }
}
