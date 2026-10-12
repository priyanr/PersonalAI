namespace PersonalAI.Core.Models.DTOs;

/// <summary>A secret as returned to the UI: the value itself is never echoed back, only whether one is set.</summary>
public record SecretDto(string Key, bool HasValue, DateTimeOffset UpdatedAt);

public record CreateSecretRequest(string Key, string Value);

public record UpdateSecretRequest(string Value);
