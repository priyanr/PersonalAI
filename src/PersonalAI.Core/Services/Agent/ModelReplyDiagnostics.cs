using System.Net;
using System.Text.Json;
using PersonalAI.Core.Models.DTOs;

namespace PersonalAI.Core.Services.Agent;

/// <summary>Wording shared by the chat, skill and agent loops for a model reply that came back empty or was cut
/// off at the output-token cap (typically a reasoning model spending its whole budget thinking), so none of
/// them mistakes it for a finished answer or a well-formed tool call.</summary>
public static class ModelReplyDiagnostics
{
    /// <summary>For a reply with no text and no tool call.</summary>
    public static string EmptyReplyNotice(string providerKey, bool hitOutputLimit, bool sawReasoning, AiTokenUsage? usage, bool canDisableThinking)
    {
        if (!hitOutputLimit)
            return "The model returned an empty response (no text and no tool call). Ask it to continue or try again.";

        var spent = usage is { OutputTokens: > 0 } ? $" ({usage.OutputTokens} tokens)" : "";
        var what = sawReasoning ? "spent its entire output budget thinking" : "used its entire output budget";
        return $"The model {what}{spent} without producing an answer or a tool call. " + OutputLimitAdvice(providerKey, canDisableThinking);
    }

    /// <summary>For a text answer that stopped at the output-token cap.</summary>
    public static string CutOffAnswerNotice(string providerKey, bool canDisableThinking)
    {
        return "The answer above was cut off at the output-token limit. " + OutputLimitAdvice(providerKey, canDisableThinking);
    }

    /// <summary>The tool result to return instead of executing <paramref name="call"/> when the output-token cap
    /// truncated it, or null when it wasn't. A truncated call either has unparseable arguments or — when the cap
    /// hit right after the tool name — none at all; the cut always lands on the last call of the reply.</summary>
    public static string? TruncatedToolCallError(AiToolCall call, IReadOnlyList<AiToolCall> calls, bool hitOutputLimit)
    {
        if (!hitOutputLimit) return null;

        string where;
        if (string.IsNullOrWhiteSpace(call.ArgumentsJson))
        {
            if (!ReferenceEquals(call, calls[^1])) return null;
            where = "before its arguments";
        }
        else
        {
            try
            {
                using var _ = JsonDocument.Parse(call.ArgumentsJson);
                return null;
            }
            catch (JsonException ex)
            {
                where = $"mid-arguments ({ex.Message})";
            }
        }

        return $"Your response hit the output-token limit, so this tool call was cut off {where} and was not run. " +
               "Re-emit it with much smaller arguments — e.g. write a large file in several smaller pieces " +
               "(create it, then append or edit) instead of all at once.";
    }

    /// <summary>The chat-facing text for a failed provider call. A 400 rejecting a request field gets a pointer
    /// to the per-model settings that can switch that field off or over.</summary>
    public static string ProviderErrorMessage(Exception ex)
    {
        return ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest } && RejectsParameter(ex.Message)
            ? ex.Message + "\n\nThis model rejected a request parameter. Open Models → Configure for this model to set " +
              "its output limit parameter, tool calling, reasoning effort or API (chat completions / responses), then try again."
            : ex.Message;
    }

    private static readonly string[] RejectedParameterPhrases =
        ["unsupported_parameter", "unsupported parameter", "not supported", "unrecognized", "extra inputs"];

    private static bool RejectsParameter(string message)
    {
        return RejectedParameterPhrases.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string OutputLimitAdvice(string providerKey, bool canDisableThinking)
    {
        return $"Raise \"Max output tokens\" for this model (Models → Configure) or for the '{providerKey}' provider in Settings → Providers" +
        (canDisableThinking ? ", or turn thinking off for this session" : "") +
        ", then try again.";
    }
}
