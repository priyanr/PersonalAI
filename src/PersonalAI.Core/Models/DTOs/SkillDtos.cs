namespace PersonalAI.Core.Models.DTOs;

public record SkillDto(
    Guid Id,
    string Name,
    string? Description,
    string Prompt,
    string ProviderKey,
    string ModelName,
    int? NumCtx,
    IReadOnlyList<string> EnabledTools,
    IReadOnlyList<string> SecretKeys,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record CreateSkillRequest(
    string Name,
    string? Description,
    string Prompt,
    string? ProviderKey,
    string ModelName,
    int? NumCtx,
    IReadOnlyList<string>? EnabledTools,
    IReadOnlyList<string>? SecretKeys);

public record UpdateSkillRequest(
    string Name,
    string? Description,
    string Prompt,
    string? ProviderKey,
    string ModelName,
    int? NumCtx,
    IReadOnlyList<string>? EnabledTools,
    IReadOnlyList<string>? SecretKeys);

public record RunSkillRequest(string? Input);

/// <summary>One tool a skill can be granted access to, as advertised for the skill editor UI.</summary>
public record AvailableToolDto(string Name, string Description);
