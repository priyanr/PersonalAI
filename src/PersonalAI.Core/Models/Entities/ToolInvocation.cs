using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.Entities;

public class ToolInvocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid RequestingMessageId { get; set; }

    /// <summary>Synthesized correlation id — Ollama does not guarantee a stable per-call id.</summary>
    public required string ToolCallId { get; set; }
    public required string ToolName { get; set; }
    public required string ArgumentsJson { get; set; }

    public ToolInvocationStatus Status { get; set; } = ToolInvocationStatus.Completed;

    /// <summary>Populated when a file tool resolves a path outside the workspace sandbox.</summary>
    public string? ResolvedPath { get; set; }
    public string? Reason { get; set; }
    public string? ResultContent { get; set; }
    public bool IsError { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }

    public ChatSession? Session { get; set; }
    public ChatMessage? RequestingMessage { get; set; }
}
