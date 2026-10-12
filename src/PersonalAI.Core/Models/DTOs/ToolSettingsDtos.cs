namespace PersonalAI.Core.Models.DTOs;

/// <summary>A tool setting field as returned to the UI: Value is populated for non-secret fields (falling
/// back to the field's default when unset); Secret fields always return Value: null and rely on HasValue.</summary>
public record ToolSettingFieldDto(string Name, string Label, string Type, bool Required, bool HasValue, string? Value, string? CustomComponentKey, string? Placeholder = null, string? Help = null);

public record ToolSettingsDto(string Name, string DisplayName, IReadOnlyList<ToolSettingFieldDto> Fields, bool IsConfigured, bool IsConnected);

public record UpdateToolConfigRequest(Dictionary<string, string?> Values);
