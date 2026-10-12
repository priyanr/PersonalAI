namespace PersonalAI.Core.Models.DTOs;

public record AiModelDto(
    string Name,
    long Size,
    string? Digest,
    DateTimeOffset? ModifiedAt,
    string? ParameterSize,
    string? QuantizationLevel,
    IReadOnlyDictionary<string, string?>? Settings = null);

/// <summary>One per-model setting the provider honors, as rendered by the Models page's Configure dialog.</summary>
public record ModelSettingFieldDto(string Name, string Label, string Type, string? DefaultValue, IReadOnlyList<string>? Options, string? Help);

public record UpdateModelSettingsRequest(Dictionary<string, string?> Values);

public record PullModelRequest(string Name);

public record PullProgressDto(string Status, string? Digest, long? Total, long? Completed);
