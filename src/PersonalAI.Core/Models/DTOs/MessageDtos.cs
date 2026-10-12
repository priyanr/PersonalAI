using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.DTOs;

public record ToolCallDto(string ToolCallId, string Name, string ArgumentsJson);

public record MessageDto(
    Guid Id,
    int SequenceNumber,
    MessageRole Role,
    string? Content,
    IReadOnlyList<ToolCallDto>? ToolCalls,
    string? ToolCallId,
    string? ToolName,
    bool IsError,
    DateTimeOffset CreatedAt);

public record SendMessageRequest(string Text);
