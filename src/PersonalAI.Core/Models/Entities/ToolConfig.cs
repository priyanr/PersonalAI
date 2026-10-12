namespace PersonalAI.Core.Models.Entities;

/// <summary>One configured field for one tool (e.g. ToolName="gmail", FieldName="ClientId"). Same shape
/// and trust model as AIProviderConfig — plaintext, local SQLite. Also used to store hidden/system
/// fields a tool manages itself (e.g. Gmail's OAuth tokens) that never appear in that tool's
/// SettingsFields and are therefore never touched by the generic PATCH endpoint.</summary>
public class ToolConfig
{
    public int Id { get; set; }
    public required string ToolName { get; set; }
    public required string FieldName { get; set; }
    public string? Value { get; set; }
}
