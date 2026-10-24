using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PersonalAI.Core.Models.DTOs;

namespace PersonalAI.Core.Services.AI.OpenAiCompatible;

// The Responses API (/responses), which some reasoning models need for function tools. Chat Completions is in the main file.
public abstract partial class OpenAiCompatibleProviderBase
{
    // "{providerKey}:{model}" pairs that refused function tools on /chat/completions with any reasoning_effort
    // we could send, so go to /responses instead.
    private static readonly ConcurrentDictionary<string, bool> ResponsesApiModels = new();

    // "{providerKey}:{model}" pairs on /responses that rejected reasoning.summary (a model without reasoning, or
    // an organization not verified for reasoning summaries), so it is left out.
    private static readonly ConcurrentDictionary<string, bool> NoReasoningSummaryModels = new();

    private bool UsesResponsesApi(string model, string modelKey)
    {
        return SupportsResponsesApi
        && configCache.GetModelValue(Key, model, ModelSettings.ApiField)?.ToLowerInvariant() switch
        {
            ModelSettings.ResponsesApi => true,
            ModelSettings.ChatCompletionsApi => false,
            _ => ResponsesApiModels.ContainsKey(modelKey)
        };
    }

    /// <summary>Tools were refused along with a reasoning_effort we sent: either the model wants "none" and the user
    /// (or the chat/agent) chose something else, or it rejected "none" itself. /chat/completions can't serve it with tools then, but
    /// /responses can.</summary>
    private bool ShouldSwitchToResponsesApi(OpenAiChatRequestWire requestBody, string errorBody, string model)
    {
        return SupportsResponsesApi
        && requestBody.Tools is not null
        && requestBody.ReasoningEffort is not null
        && IsToolsNeedNoReasoningError(errorBody)
        && configCache.GetModelValue(Key, model, ModelSettings.ApiField) is null;
    }

    private async IAsyncEnumerable<AiChatChunk> ResponsesStreamAsync(
        string baseUrl, string? apiKey, string model, string modelKey,
        IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools, int maxOutputTokens, string? effort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var summary = effort == "none" || NoReasoningSummaryModels.ContainsKey(modelKey) ? null : "auto";
        var requestBody = new OpenAiResponsesRequestWire
        {
            Model = model,
            Stream = true,
            Store = false,
            Input = [.. messages.SelectMany(ToResponsesInputItems)],
            Tools = tools.Count == 0 || configCache.ToolsDisabled(Key, model) ? null : [.. tools.Select(ToResponsesTool)],
            MaxOutputTokens = ResolveOutputLimitParameter(model, modelKey) == ModelSettings.OmitParameter ? null : maxOutputTokens,
            Reasoning = effort is null && summary is null
                ? null
                : new OpenAiResponsesReasoningWire { Effort = effort, Summary = summary }
        };

        var url = $"{baseUrl}/responses";
        var response = await PostStreamingAsync(url, apiKey, JsonSerializer.Serialize(requestBody, JsonOptions), ct);
        if (response.StatusCode == HttpStatusCode.BadRequest && requestBody.Reasoning?.Summary is not null)
        {
            // Reasoning summaries are an extra we ask for, not something the chat needs: a model without reasoning,
            // or an organization not verified for summaries, refuses them — so retry without and remember.
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            var summaryRefused = errorBody.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                && (requestBody.Reasoning.Effort is null || errorBody.Contains("summar", StringComparison.OrdinalIgnoreCase));
            if (!summaryRefused)
                throw new HttpRequestException(
                    $"Response status code does not indicate success: 400 (Bad Request). {Truncate(errorBody)}",
                    null, HttpStatusCode.BadRequest);

            NoReasoningSummaryModels[modelKey] = true;
            requestBody.Reasoning = requestBody.Reasoning.Effort is null
                ? null
                : new OpenAiResponsesReasoningWire { Effort = requestBody.Reasoning.Effort };
            response = await PostStreamingAsync(url, apiKey, JsonSerializer.Serialize(requestBody, JsonOptions), ct);
        }

        using var _ = response;
        await EnsureSuccessWithBodyAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Keyed by output_index so the calls come out in the order the model made them.
        var toolCalls = new SortedDictionary<int, OpenAiResponsesOutputItemWire>();
        AiTokenUsage? usage = null;
        var hitOutputLimit = false;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;
            if (payload.Length == 0) continue;

            OpenAiResponsesEventWire? evt;
            try
            {
                evt = JsonSerializer.Deserialize<OpenAiResponsesEventWire>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            switch (evt?.Type)
            {
                case "response.output_text.delta" when evt.DeltaText is { Length: > 0 } text:
                    yield return new AiChatChunk(text, null, false);
                    break;

                // Summaries of hidden reasoning (OpenAI models), or raw reasoning text (open-weight models).
                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta"
                    when evt.DeltaText is { Length: > 0 } reasoning:
                    yield return new AiChatChunk(null, null, false, ReasoningDelta: reasoning);
                    break;

                case "response.reasoning_summary_part.added" when evt.SummaryIndex > 0:
                    yield return new AiChatChunk(null, null, false, ReasoningDelta: "\n\n");
                    break;

                // The finished item carries the complete arguments, so the function_call_arguments.delta
                // fragments needn't be stitched together.
                case "response.output_item.done" when evt.Item is { Type: "function_call" } item:
                    toolCalls[evt.OutputIndex] = item;
                    break;

                case "response.completed" or "response.incomplete":
                    if (evt.Response?.Usage is { } u)
                        usage = new AiTokenUsage(u.InputTokens, u.OutputTokens);
                    if (evt.Response?.IncompleteDetails?.Reason == "max_output_tokens")
                        hitOutputLimit = true;
                    break;

                case "response.failed":
                    throw new HttpRequestException($"The model failed to respond: {evt.Response?.Error?.Message ?? "unknown error"}");

                case "error":
                    throw new HttpRequestException($"The model failed to respond: {evt.Message ?? payload}");
            }
        }

        var finalToolCalls = toolCalls.Values
            .Select(item => new AiToolCall(
                item.CallId ?? Guid.NewGuid().ToString("N"),
                item.Name ?? "",
                // As on /chat/completions: blank means "no arguments" unless the output cap cut the call off.
                !string.IsNullOrEmpty(item.Arguments) ? item.Arguments : hitOutputLimit ? "" : "{}"))
            .ToList();
        yield return new AiChatChunk(null, finalToolCalls.Count > 0 ? finalToolCalls : null, true, usage, HitOutputLimit: hitOutputLimit);
    }

    /// <summary>A chat message as Responses API input items: its text as a role message, plus a function_call item
    /// per tool call an assistant message made; a tool result becomes a function_call_output.</summary>
    private static IEnumerable<OpenAiResponsesInputItemWire> ToResponsesInputItems(AiChatMessage m)
    {
        if (m.Role == "tool")
        {
            yield return new OpenAiResponsesInputItemWire { Type = "function_call_output", CallId = m.ToolCallId ?? "", Output = m.Content ?? "" };
            yield break;
        }

        if (m.Role != "assistant" || !string.IsNullOrEmpty(m.Content) || m.ToolCalls is not { Count: > 0 })
            yield return new OpenAiResponsesInputItemWire { Role = m.Role, Content = m.Content ?? "" };

        foreach (var tc in m.ToolCalls ?? [])
            yield return new OpenAiResponsesInputItemWire { Type = "function_call", CallId = tc.ToolCallId, Name = tc.Name, Arguments = tc.WireArgumentsJson };
    }

    private static OpenAiResponsesToolWire ToResponsesTool(AiToolDefinition t)
    {
        return new()
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = JsonDocument.Parse(t.ParametersSchemaJson).RootElement.Clone()
        };
    }
}
