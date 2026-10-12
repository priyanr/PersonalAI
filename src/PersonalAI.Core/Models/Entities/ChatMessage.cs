using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.Entities;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int SequenceNumber { get; set; }
    public MessageRole Role { get; set; }
    public string? Content { get; set; }

    /// <summary>JSON array of {toolCallId, name, argumentsJson} — only set on Assistant messages that made tool calls.</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>Only set on Tool-role result messages; correlates back to the requesting call.</summary>
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }

    public bool IsError { get; set; }

    /// <summary>A notice meant for the user only (e.g. "the model hit its output-token limit — raise it in
    /// Settings"): shown in the transcript, but never sent to the model or folded into a history summary.</summary>
    public bool ExcludeFromContext { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ChatSession? Session { get; set; }
}
