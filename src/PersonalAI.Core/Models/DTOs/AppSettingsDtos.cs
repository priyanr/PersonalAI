namespace PersonalAI.Core.Models.DTOs;

public record AppSettingsDto(string WebSearchGuidance, bool IsDefault, bool ThinkingEnabled);

public record UpdateAppSettingsRequest(string? WebSearchGuidance, bool ThinkingEnabled);
