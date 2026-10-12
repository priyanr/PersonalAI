using System.Text;
using System.Text.Json;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Api.Streaming;

public static class SseEventWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void PrepareResponse(HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteAsync(HttpResponse response, AgentStreamEvent evt, CancellationToken ct)
    {
        object payload = evt switch
        {
            TextDeltaEvent e => new { type = "token", text = e.Text },
            ReasoningDeltaEvent e => new { type = "reasoning", text = e.Text },
            AgentRunStartedEvent e => new { type = "run-started", runId = e.RunId },
            LlmCallEvent e => new { type = "llm-call", totalCalls = e.TotalCalls },
            TokenUsageEvent e => new { type = "token-usage", totalInputTokens = e.TotalInputTokens, totalOutputTokens = e.TotalOutputTokens },
            TurnStartedEvent e => new { type = "turn-started", afterSequenceNumber = e.AfterSequenceNumber },
            ToolResultEvent e => new
            {
                type = "tool-result",
                toolInvocationId = e.ToolInvocationId,
                toolName = e.ToolName,
                resultContent = e.ResultContent,
                isError = e.IsError
            },
            ApprovalRequiredEvent e => new
            {
                type = "approval-required",
                toolInvocationId = e.ToolInvocationId,
                toolName = e.ToolName,
                resolvedPath = e.ResolvedPath,
                reason = e.Reason
            },
            ToolProgressEvent e => new
            {
                type = "tool-progress",
                toolInvocationId = e.ToolInvocationId,
                toolName = e.ToolName,
                message = e.Message
            },
            CompactionCompletedEvent e => new { type = "compacted", foldedMessages = e.FoldedMessages },
            DoneEvent => new { type = "done" },
            ErrorEvent e => new { type = "error", message = e.Message },
            _ => new { type = "unknown" }
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>Streams a background chat turn to this response: replay of everything so far, then live.
    /// The client disconnecting only ends this subscription; the turn itself keeps running.</summary>
    public static async Task WriteTurnAsync(HttpResponse response, ChatTurn turn, CancellationToken ct)
    {
        PrepareResponse(response);
        try
        {
            await foreach (var evt in turn.SubscribeAsync(ct))
                await WriteAsync(response, evt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away (reload, navigation) — expected, and deliberately not a turn cancellation.
        }
    }

    public static async Task WritePullProgressAsync(HttpResponse response, PullProgressDto progress, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(progress, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteErrorAndCloseAsync(HttpResponse response, string message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { type = "error", message }, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }
}
