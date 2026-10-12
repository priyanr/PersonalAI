using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAI.Core.Services.AI.Ollama;

// Internal wire DTOs for Ollama's /api/chat, /api/tags, /api/pull.
// NOTE: tool_calls shape (whether it streams incrementally or arrives whole in the terminating
// chunk, and whether "arguments" is an object vs. a string) has shifted across Ollama releases —
// verify against the installed version if tool-calling parsing misbehaves.

internal sealed class OllamaMessageWire
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OllamaToolCallWire>? ToolCalls { get; set; }

    /// <summary>Thinking tokens, streamed separately from content when the request sets think=true.
    /// Read-only in practice: never populated on outgoing messages, so it's omitted from requests.</summary>
    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thinking { get; set; }
}

internal sealed class OllamaToolCallWire
{
    [JsonPropertyName("function")]
    public OllamaFunctionCallWire? Function { get; set; }
}

internal sealed class OllamaFunctionCallWire
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Ollama sends this as a parsed JSON object, not a string like OpenAI's wire format.</summary>
    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; set; }
}

internal sealed class OllamaChatRequestWire
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<OllamaMessageWire> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    public List<OllamaToolWire>? Tools { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("options")]
    public OllamaOptionsWire? Options { get; set; }

    /// <summary>Whether the model may emit extended reasoning ("thinking") tokens: false, true, or a level
    /// ("low", "medium", "high" — honored by models with thinking levels such as gpt-oss, treated as true by
    /// other thinking models). Only meaningful for models that advertise the "thinking" capability.</summary>
    [JsonPropertyName("think")]
    public object Think { get; set; } = false;
}

internal sealed class OllamaOptionsWire
{
    [JsonPropertyName("num_ctx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumCtx { get; set; }

    /// <summary>Caps total generated tokens so a model can't silently burn the whole call
    /// (e.g. rambling in hidden thinking) without ever producing a visible answer.</summary>
    [JsonPropertyName("num_predict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumPredict { get; set; }
}

internal sealed class OllamaToolWire
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OllamaFunctionDefWire Function { get; set; } = new();
}

internal sealed class OllamaFunctionDefWire
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}

internal sealed class OllamaChatChunkWire
{
    [JsonPropertyName("message")]
    public OllamaMessageWire? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    /// <summary>Why generation ended — "stop", or "length" when num_predict cut it off. Only on the done chunk.</summary>
    [JsonPropertyName("done_reason")]
    public string? DoneReason { get; set; }

    /// <summary>Prompt tokens evaluated — only on the final (done) chunk.</summary>
    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    /// <summary>Tokens generated — only on the final (done) chunk.</summary>
    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }
}

internal sealed class OllamaTagsResponseWire
{
    [JsonPropertyName("models")]
    public List<OllamaModelWire>? Models { get; set; }
}

internal sealed class OllamaModelWire
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("modified_at")]
    public DateTimeOffset? ModifiedAt { get; set; }

    [JsonPropertyName("details")]
    public OllamaModelDetailsWire? Details { get; set; }
}

internal sealed class OllamaModelDetailsWire
{
    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }
}

internal sealed class OllamaPullRequestWire
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;
}

internal sealed class OllamaPullProgressWire
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("completed")]
    public long? Completed { get; set; }
}
