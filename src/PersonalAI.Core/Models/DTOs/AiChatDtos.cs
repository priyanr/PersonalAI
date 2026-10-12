namespace PersonalAI.Core.Models.DTOs;

/// <summary>A single chat message as sent to / reconstructed from a provider's chat completion API.
/// ToolCallId is only set on Tool-role messages — OpenAI-compatible providers require it to correlate
/// a tool result back to the call that requested it; Ollama's wire format ignores it. ReasoningContent is
/// only set on an Assistant tool-call message of the turn in progress: DeepSeek's thinking mode expects the
/// thinking behind a tool call to be passed back while that turn's tool loop continues (and dropped after).</summary>
public record AiChatMessage(string Role, string? Content, IReadOnlyList<AiToolCall>? ToolCalls = null, string? ToolCallId = null, string? ReasoningContent = null);

/// <summary>A tool call requested by the model. ToolCallId is synthesized locally for providers
/// (like Ollama) that don't guarantee a stable per-call id the way OpenAI's wire format does.</summary>
public record AiToolCall(string ToolCallId, string Name, string ArgumentsJson);

/// <summary>One decoded chunk of a streaming chat completion. Usage is only set on the final chunk, and only
/// when the provider reported it. ReasoningDelta carries the model's thinking tokens (DeepSeek's
/// reasoning_content, Ollama's message.thinking) — shown live and never persisted; only DeepSeek gets it
/// back, within the same turn (see AiChatMessage.ReasoningContent).
/// HitOutputLimit is only set on the final chunk, when generation stopped because it reached the
/// output-token cap (OpenAI finish_reason / Ollama done_reason "length") rather than finishing.</summary>
public record AiChatChunk(
    string? ContentDelta,
    IReadOnlyList<AiToolCall>? ToolCalls,
    bool Done,
    AiTokenUsage? Usage = null,
    string? ReasoningDelta = null,
    bool HitOutputLimit = false);

/// <summary>Token counts one provider call reported: prompt (input) and completion (output).</summary>
public record AiTokenUsage(int InputTokens, int OutputTokens);

/// <summary>A tool definition as advertised to a provider's tools[] array.</summary>
public record AiToolDefinition(string Name, string Description, string ParametersSchemaJson);

public record AiModelInfo(
    string Name,
    long Size,
    string? Digest,
    DateTimeOffset? ModifiedAt,
    string? ParameterSize,
    string? QuantizationLevel);

/// <summary>One decoded progress update from a streaming model-pull operation. Only meaningful for
/// providers that support local model management (e.g. Ollama).</summary>
public record AiPullProgress(string Status, string? Digest, long? Total, long? Completed);
