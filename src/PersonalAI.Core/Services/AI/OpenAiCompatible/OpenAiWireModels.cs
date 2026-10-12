using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAI.Core.Services.AI.OpenAiCompatible;

// Internal wire DTOs for the OpenAI-compatible /chat/completions and /models APIs, shared by
// OpenAI and DeepSeek. Streaming tool calls arrive as argument-string FRAGMENTS keyed by index —
// see OpenAiCompatibleProviderBase for the accumulation logic that reassembles them.

internal sealed class OpenAiMessageWire
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCallWire>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    /// <summary>DeepSeek only: the thinking behind an assistant tool call, passed back within the same turn.</summary>
    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; set; }
}

internal sealed class OpenAiToolCallWire
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionCallWire Function { get; set; } = new();
}

internal sealed class OpenAiFunctionCallWire
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>A JSON-encoded string, unlike Ollama which sends a parsed object.</summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";
}

internal sealed class OpenAiChatRequestWire
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<OpenAiMessageWire> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<OpenAiToolWire>? Tools { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("max_completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiStreamOptionsWire? StreamOptions { get; set; }

    /// <summary>DeepSeek's thinking-mode switch ({"type":"enabled"|"disabled"}). Null = omit, leaving the
    /// model's own default; standard OpenAI has no such parameter.</summary>
    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiThinkingWire? Thinking { get; set; }
}

internal sealed class OpenAiThinkingWire
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

internal sealed class OpenAiStreamOptionsWire
{
    /// <summary>Asks for one extra chunk after the last choice chunk, with empty choices and the call's usage.</summary>
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; }
}

internal sealed class OpenAiToolWire
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionDefWire Function { get; set; } = new();
}

internal sealed class OpenAiFunctionDefWire
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}

internal sealed class OpenAiChatChunkWire
{
    [JsonPropertyName("choices")]
    public List<OpenAiChoiceWire>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiUsageWire? Usage { get; set; }
}

internal sealed class OpenAiUsageWire
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}

internal sealed class OpenAiChoiceWire
{
    [JsonPropertyName("delta")]
    public OpenAiDeltaWire? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class OpenAiDeltaWire
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Thinking tokens, streamed separately from content by DeepSeek (and vLLM/SGLang reasoning parsers).
    /// A JsonElement, not a string, like <see cref="Reasoning"/>: a gateway that sends a structured value must
    /// not fail the whole chunk's deserialization and lose its content/tool-call fragments with it.</summary>
    [JsonPropertyName("reasoning_content")]
    public JsonElement ReasoningContent { get; set; }

    /// <summary>The same thing under the name OpenRouter and some other gateways use.</summary>
    [JsonPropertyName("reasoning")]
    public JsonElement Reasoning { get; set; }

    /// <summary>The reasoning text when either field carries a plain string; anything structured is ignored.</summary>
    public string? ReasoningText =>
        ReasoningContent.ValueKind == JsonValueKind.String ? ReasoningContent.GetString()
        : Reasoning.ValueKind == JsonValueKind.String ? Reasoning.GetString()
        : null;

    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCallDeltaWire>? ToolCalls { get; set; }
}

internal sealed class OpenAiToolCallDeltaWire
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public OpenAiFunctionCallDeltaWire? Function { get; set; }
}

internal sealed class OpenAiFunctionCallDeltaWire
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>A partial fragment of the eventual JSON-encoded arguments string — append, don't replace.</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

internal sealed class OpenAiModelsResponseWire
{
    [JsonPropertyName("data")]
    public List<OpenAiModelWire>? Data { get; set; }
}

internal sealed class OpenAiModelWire
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

// ---- /responses (OpenAI Responses API) ----
// Needed by reasoning models that refuse function tools on /chat/completions unless reasoning is off.
// The conversation goes in as a flat list of items: role messages, function_call items the model made,
// and function_call_output items with the tool results. Streaming is typed SSE events rather than deltas.

internal sealed class OpenAiResponsesRequestWire
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("input")]
    public List<OpenAiResponsesInputItemWire> Input { get; set; } = [];

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiResponsesToolWire>? Tools { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    /// <summary>Nothing is kept server-side: every call carries the whole conversation, as with /chat/completions.</summary>
    [JsonPropertyName("store")]
    public bool Store { get; set; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiResponsesReasoningWire? Reasoning { get; set; }
}

internal sealed class OpenAiResponsesReasoningWire
{
    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; set; }

    /// <summary>"auto" asks for a readable summary of the reasoning, streamed as reasoning deltas.</summary>
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }
}

/// <summary>One input item: a role message (Role + Content), a function_call (CallId + Name + Arguments)
/// or a function_call_output (CallId + Output). Unused fields are left null and not written.</summary>
internal sealed class OpenAiResponsesInputItemWire
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>The id pairing a function_call with its function_call_output. The item's own "id" is deliberately
    /// never sent: with store=false it would reference a server-side item that doesn't exist.</summary>
    [JsonPropertyName("call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }

    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; set; }
}

/// <summary>A function tool — flat, unlike /chat/completions which nests these under "function".</summary>
internal sealed class OpenAiResponsesToolWire
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }

    /// <summary>Off: our schemas aren't written for strict mode (every property required, no extras).</summary>
    [JsonPropertyName("strict")]
    public bool Strict { get; set; }
}

/// <summary>One streamed event. Only the fields of the event types we act on are mapped.</summary>
internal sealed class OpenAiResponsesEventWire
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Text for *.delta events. A JsonElement so an unexpected shape can't fail the whole event.</summary>
    [JsonPropertyName("delta")]
    public JsonElement Delta { get; set; }

    public string? DeltaText => Delta.ValueKind == JsonValueKind.String ? Delta.GetString() : null;

    [JsonPropertyName("output_index")]
    public int OutputIndex { get; set; }

    [JsonPropertyName("summary_index")]
    public int SummaryIndex { get; set; }

    [JsonPropertyName("item")]
    public OpenAiResponsesOutputItemWire? Item { get; set; }

    [JsonPropertyName("response")]
    public OpenAiResponsesResponseWire? Response { get; set; }

    /// <summary>Set on a top-level "error" event.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class OpenAiResponsesOutputItemWire
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

internal sealed class OpenAiResponsesResponseWire
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiResponsesUsageWire? Usage { get; set; }

    [JsonPropertyName("incomplete_details")]
    public OpenAiResponsesIncompleteDetailsWire? IncompleteDetails { get; set; }

    [JsonPropertyName("error")]
    public OpenAiResponsesErrorWire? Error { get; set; }
}

internal sealed class OpenAiResponsesUsageWire
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

internal sealed class OpenAiResponsesIncompleteDetailsWire
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

internal sealed class OpenAiResponsesErrorWire
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
