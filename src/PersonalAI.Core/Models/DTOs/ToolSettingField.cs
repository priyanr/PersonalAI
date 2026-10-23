namespace PersonalAI.Core.Models.DTOs;

public enum ToolSettingFieldType
{
    Text,
    Secret,
    Url,
    Number,
    Boolean,
    /// <summary>Multi-line text (e.g. one entry per line).</summary>
    TextArea,
    Custom
}

/// <summary>Declarative description of one piece of configuration a tool needs (e.g. an OAuth client id) —
/// modeled on ProviderConfigField for AI providers. A Custom field renders a tool-specific UI component
/// (identified by CustomComponentKey) instead of a generic input, and is never accepted by the generic
/// PATCH endpoint. Placeholder and Help are display-only: unlike DefaultValue, a Placeholder is never
/// reported as the field's value, so it suits a blank-means-fallback field.</summary>
public record ToolSettingField(
    string Name, string Label, ToolSettingFieldType Type, bool Required,
    string? DefaultValue = null, string? CustomComponentKey = null,
    string? Placeholder = null, string? Help = null);
