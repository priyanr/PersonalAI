using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.DTOs;

public record ToolInvocationDto(
    Guid Id,
    string ToolName,
    string ArgumentsJson,
    ToolInvocationStatus Status,
    string? ResolvedPath,
    string? Reason,
    string? ResultContent,
    bool IsError);

public record ResolveToolInvocationRequest(bool Approved);
