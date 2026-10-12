namespace PersonalAI.Core.Models.DTOs;

/// <summary>A config field as returned to the UI: Value is populated for non-secret fields (falling back
/// to the field's default when unset); Secret fields always return Value: null and rely on HasValue.</summary>
public record ProviderConfigFieldDto(string Name, string Label, string Type, bool Required, bool HasValue, string? Value);

public record AIProviderDto(string Key, string DisplayName, IReadOnlyList<ProviderConfigFieldDto> Fields, bool IsConfigured);

public record UpdateAIProviderConfigRequest(Dictionary<string, string?> Values);
