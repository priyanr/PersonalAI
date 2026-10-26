using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.AI.OpenAiCompatible;

/// <summary>Shared implementation of the OpenAI /chat/completions + /models wire protocol, used by
/// both OpenAiProvider and DeepSeekProvider (DeepSeek's API is OpenAI-compatible). Providers that also
/// serve the Responses API (OpenAI, Azure) can send a model to /responses instead — see
/// <see cref="SupportsResponsesApi"/>.</summary>
public abstract partial class OpenAiCompatibleProviderBase(HttpClient httpClient, AIProviderConfigCache configCache, IOptions<AgentSettings> agentSettings) : IAIProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract string DefaultBaseUrl { get; }
    public abstract IReadOnlyList<ProviderConfigField> ConfigFields { get; }

    protected virtual bool UseMaxCompletionTokens(string model)
    {
        return false;
    }

    /// <summary>Lets a subclass map the chat loop's thinking switch onto a provider-specific request field.
    /// The default does nothing: standard OpenAI has no on/off switch for reasoning.</summary>
    private protected virtual void ApplyThinking(OpenAiChatRequestWire requestBody, bool? think) { }

    /// <summary>Whether assistant messages carry their ReasoningContent back on the wire. Off by default:
    /// standard OpenAI rejects unknown message fields.</summary>
    private protected virtual bool SendsReasoningBack => false;

    public virtual bool SupportsThinkingToggle => false;

    /// <summary>Whether this backend also serves /responses, which some reasoning models need for function tools.</summary>
    protected virtual bool SupportsResponsesApi => true;

    public virtual IReadOnlyList<ProviderConfigField> ModelSettingFields => ModelSettings.OpenAiCompatibleFields;

    // "{providerKey}:{model}" pairs known to need reasoning_effort "none" whenever tools are sent.
    // Static because providers are transient typed-HttpClient instances.
    private static readonly ConcurrentDictionary<string, bool> NoReasoningWithToolsModels = new();

    // "{providerKey}:{model}" → the output-limit parameter a 400 told us to use instead of the one we sent.
    private static readonly ConcurrentDictionary<string, string> LearnedOutputLimitParameters = new();

    // Phrases the various backends use when rejecting a request field they don't accept.
    private static readonly string[] UnsupportedParameterPhrases =
        ["unsupported", "not supported", "unrecognized", "unknown", "extra inputs", "not permitted", "not allowed"];

    private const int MaxCorrectiveRetries = 3;

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). {Truncate(body)}",
            null, response.StatusCode);
    }

    private static string Truncate(string body)
    {
        return body.Length > 1000 ? body[..1000] : body;
    }

    private static bool IsToolsNeedNoReasoningError(string body)
    {
        return body.Contains("reasoning_effort", StringComparison.Ordinal)
        && body.Contains("'none'", StringComparison.Ordinal);
    }


    /// <summary>The caller's reasoning effort (a chat's or agent's own choice), else the model's pinned
    /// ReasoningEffort, else null — send nothing and let the model use its default.</summary>
    private string? ResolveReasoningEffort(string model, string? reasoningEffort)
    {
        return (reasoningEffort ?? configCache.GetModelValue(Key, model, ModelSettings.ReasoningEffortField))?.ToLowerInvariant();
    }


    /// <summary>A 400 rejecting <paramref name="parameter"/> itself (not, say, its value being too large).</summary>
    private static bool IsUnsupportedParameterError(string body, string parameter)
    {
        return body.Contains(parameter, StringComparison.Ordinal)
        && UnsupportedParameterPhrases.Any(p => body.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveOutputLimitParameter(string model, string modelKey)
    {
        return configCache.GetModelValue(Key, model, ModelSettings.OutputLimitParameterField)?.ToLowerInvariant()
        ?? (LearnedOutputLimitParameters.TryGetValue(modelKey, out var learned) ? learned : null)
        ?? (UseMaxCompletionTokens(model) ? ModelSettings.MaxCompletionTokensParameter : ModelSettings.MaxTokensParameter);
    }

    private static void ApplyOutputLimit(OpenAiChatRequestWire requestBody, string parameter, int maxOutputTokens)
    {
        requestBody.MaxTokens = parameter == ModelSettings.MaxTokensParameter ? maxOutputTokens : null;
        requestBody.MaxCompletionTokens = parameter == ModelSettings.MaxCompletionTokensParameter ? maxOutputTokens : null;
    }

    /// <summary>Adjusts <paramref name="requestBody"/> for a 400 whose cause we recognise and can work around,
    /// remembering the fix for later calls to the same model. False when the error isn't one of those — or the
    /// offending field is one the user pinned on the Models page, which is theirs to change.</summary>
    private bool TryFixBadRequest(OpenAiChatRequestWire requestBody, string errorBody, string model, string modelKey, int maxOutputTokens)
    {
        // Some reasoning models (e.g. Azure's gpt-5.6-sol) apply a server-side default reasoning_effort on some
        // prompts and then refuse function tools with it on /chat/completions, asking for reasoning_effort "none"
        // instead. Deployment names are arbitrary, so this can't be predicted from the model name.
        if (requestBody.Tools is not null && requestBody.ReasoningEffort is null && IsToolsNeedNoReasoningError(errorBody))
        {
            NoReasoningWithToolsModels[modelKey] = true;
            requestBody.ReasoningEffort = "none";
            return true;
        }

        // Newer OpenAI models reject max_tokens ("Use 'max_completion_tokens' instead"); most other models behind
        // the same endpoints reject max_completion_tokens. Name heuristics can't keep up, so swap and remember.
        if (configCache.GetModelValue(Key, model, ModelSettings.OutputLimitParameterField) is not null)
            return false;

        var swapTo =
            requestBody.MaxTokens is not null && IsUnsupportedParameterError(errorBody, ModelSettings.MaxTokensParameter)
                ? ModelSettings.MaxCompletionTokensParameter
            : requestBody.MaxCompletionTokens is not null && IsUnsupportedParameterError(errorBody, ModelSettings.MaxCompletionTokensParameter)
                ? ModelSettings.MaxTokensParameter
            : null;
        if (swapTo is null)
            return false;

        LearnedOutputLimitParameters[modelKey] = swapTo;
        ApplyOutputLimit(requestBody, swapTo, maxOutputTokens);
        return true;
    }

    private Task<HttpResponseMessage> SendChatRequestAsync(
        string baseUrl, string? apiKey, OpenAiChatRequestWire requestBody, CancellationToken ct)
    {
        return PostStreamingAsync($"{baseUrl}/chat/completions", apiKey, JsonSerializer.Serialize(requestBody, JsonOptions), ct);
    }

    private async Task<HttpResponseMessage> PostStreamingAsync(string url, string? apiKey, string json, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(apiKey))
            ApplyAuth(httpRequest, apiKey);
        return await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    protected (string BaseUrl, string? ApiKey) ResolveConnection()
    {
        return (
            NormalizeBaseUrl((configCache.GetValue(Key, "BaseUrl") is { Length: > 0 } url ? url : DefaultBaseUrl).TrimEnd('/')),
            configCache.GetValue(Key, "ApiKey"));
    }

    protected string? GetConfigValue(string field)
    {
        return configCache.GetValue(Key, field);
    }

    /// <summary>Lets a subclass fix up a user-entered base URL (e.g. append a path suffix).</summary>
    protected virtual string NormalizeBaseUrl(string baseUrl)
    {
        return baseUrl;
    }

    /// <summary>Attaches credentials to an outgoing request. Default is OpenAI-style bearer auth.</summary>
    protected virtual void ApplyAuth(HttpRequestMessage request, string apiKey)
    {
        apiKey = apiKey.Trim();
        if (apiKey.Any(c => c > 126 || c < 33))
            throw new InvalidOperationException(
                $"The {DisplayName} API key contains non-ASCII or whitespace characters (often a masked/placeholder value or a bad paste). Re-enter the key under Settings → Providers.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async IAsyncEnumerable<AiChatChunk> ChatStreamAsync(
        string model,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        int? numCtx,
        bool? think,
        string? reasoningEffort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (baseUrl, apiKey) = ResolveConnection();
        var effort = ResolveReasoningEffort(model, reasoningEffort);
        var maxOutputTokens = configCache.ResolveMaxOutputTokens(Key, model, agentSettings.Value.MaxOutputTokens);
        var modelKey = $"{Key}:{model}";

        if (UsesResponsesApi(model, modelKey))
        {
            await foreach (var chunk in ResponsesStreamAsync(baseUrl, apiKey, model, modelKey, messages, tools, maxOutputTokens, effort, ct))
                yield return chunk;
            yield break;
        }

        var requestBody = new OpenAiChatRequestWire
        {
            Model = model,
            Stream = true,
            Messages = [.. messages.Select(m => ToWireMessage(m, SendsReasoningBack))],
            Tools = tools.Count == 0 || configCache.ToolsDisabled(Key, model) ? null : [.. tools.Select(ToWireTool)],
            StreamOptions = new OpenAiStreamOptionsWire { IncludeUsage = true }
        };
        ApplyOutputLimit(requestBody, ResolveOutputLimitParameter(model, modelKey), maxOutputTokens);
        ApplyThinking(requestBody, configCache.ResolveThink(Key, model, think));

        if (effort is not null)
            requestBody.ReasoningEffort = effort;
        else if (requestBody.Tools is not null && NoReasoningWithToolsModels.ContainsKey(modelKey))
            requestBody.ReasoningEffort = "none";

        var response = await SendChatRequestAsync(baseUrl, apiKey, requestBody, ct);
        var switchToResponsesApi = false;
        for (var retry = 0; retry < MaxCorrectiveRetries && response.StatusCode == HttpStatusCode.BadRequest; retry++)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (ShouldSwitchToResponsesApi(requestBody, errorBody, model))
            {
                ResponsesApiModels[modelKey] = true;
                switchToResponsesApi = true;
                break;
            }
            if (!TryFixBadRequest(requestBody, errorBody, model, modelKey, maxOutputTokens))
                throw new HttpRequestException(
                    $"Response status code does not indicate success: 400 (Bad Request). {Truncate(errorBody)}",
                    null, HttpStatusCode.BadRequest);
            response = await SendChatRequestAsync(baseUrl, apiKey, requestBody, ct);
        }

        if (switchToResponsesApi)
        {
            await foreach (var chunk in ResponsesStreamAsync(baseUrl, apiKey, model, modelKey, messages, tools, maxOutputTokens, effort, ct))
                yield return chunk;
            yield break;
        }

        using var _ = response;
        await EnsureSuccessWithBodyAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var accumulators = new Dictionary<int, ToolCallAccumulator>();
        AiTokenUsage? usage = null;
        var hitOutputLimit = false;
        var finished = false;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                finished = true;
                break;
            }
            if (payload.Length == 0) continue;

            OpenAiChatChunkWire? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OpenAiChatChunkWire>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            // The upstream model failed after the 200 was sent. Ignoring it would hand the loop whatever was
            // streamed so far — typically a tool call cut off mid-arguments, or an empty reply.
            if (chunk?.Error is not null || chunk?.Choices?.FirstOrDefault()?.FinishReason == "error")
                throw new HttpRequestException($"The model failed mid-response: {chunk?.Error?.Message ?? "unknown error"}");

            // With include_usage the usage arrives on its own chunk (empty choices) after the finish_reason
            // chunk, so read on to [DONE] rather than stopping at finish_reason.
            if (chunk?.Usage is { } u)
                usage = new AiTokenUsage(u.PromptTokens, u.CompletionTokens);

            var choice = chunk?.Choices?.FirstOrDefault();
            if (choice is null) continue;

            if (choice.FinishReason is not null)
                finished = true;
            if (choice.FinishReason == "length")
                hitOutputLimit = true;

            var reasoning = choice.Delta?.ReasoningText;
            if (!string.IsNullOrEmpty(reasoning))
                yield return new AiChatChunk(null, null, false, ReasoningDelta: reasoning);

            if (!string.IsNullOrEmpty(choice.Delta?.Content))
                yield return new AiChatChunk(choice.Delta!.Content, null, false);

            if (choice.Delta?.ToolCalls is { Count: > 0 })
            {
                foreach (var fragment in choice.Delta.ToolCalls)
                {
                    if (!accumulators.TryGetValue(fragment.Index, out var acc))
                    {
                        acc = new ToolCallAccumulator();
                        accumulators[fragment.Index] = acc;
                    }

                    if (!string.IsNullOrEmpty(fragment.Id)) acc.Id = fragment.Id;
                    if (!string.IsNullOrEmpty(fragment.Function?.Name)) acc.Name = fragment.Function!.Name;
                    if (fragment.Function?.Arguments is { } args) acc.Arguments.Append(args);
                }
            }
        }

        // A connection dropped mid-call leaves partial arguments that would run as a malformed call.
        if (accumulators.Count > 0 && !finished)
            throw new HttpRequestException("The model's response stream ended before its tool call was complete. Try again.");

        if (accumulators.Count > 0)
        {
            var finalToolCalls = accumulators
                .OrderBy(kv => kv.Key)
                .Select(kv => new AiToolCall(
                    kv.Value.Id ?? Guid.NewGuid().ToString("N"),
                    kv.Value.Name ?? "",
                    // Blank arguments default to "{}" — except when the output cap cut the reply, where a
                    // blank means the call was truncated right after its name and must not run as a no-arg call.
                    kv.Value.Arguments.Length > 0 ? kv.Value.Arguments.ToString() : hitOutputLimit ? "" : "{}"))
                .ToList();
            yield return new AiChatChunk(null, finalToolCalls, true, usage, HitOutputLimit: hitOutputLimit);
        }
        else
        {
            yield return new AiChatChunk(null, null, true, usage, HitOutputLimit: hitOutputLimit);
        }
    }


    public virtual async Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var (baseUrl, apiKey) = ResolveConnection();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        if (!string.IsNullOrEmpty(apiKey))
            ApplyAuth(httpRequest, apiKey);

        using var response = await httpClient.SendAsync(httpRequest, ct);
        await EnsureSuccessWithBodyAsync(response, ct);

        var body = await response.Content.ReadFromJsonAsync<OpenAiModelsResponseWire>(JsonOptions, ct);
        return body?.Data?.Select(m => new AiModelInfo(m.Id, 0, null, null, null, null)).ToList() ?? [];
    }

    public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct)
    {
        throw new NotSupportedException($"{DisplayName} does not support pulling local models.");
    }

    private static OpenAiMessageWire ToWireMessage(AiChatMessage m, bool sendReasoning)
    {
        return new()
        {
            Role = m.Role,
            Content = m.Content ?? "",
            ToolCallId = m.Role == "tool" ? m.ToolCallId : null,
            ReasoningContent = sendReasoning && m.Role == "assistant" && !string.IsNullOrEmpty(m.ReasoningContent) ? m.ReasoningContent : null,
            ToolCalls = m.ToolCalls is { Count: > 0 }
                ? [.. m.ToolCalls.Select(tc => new OpenAiToolCallWire
                {
                    Id = tc.ToolCallId,
                    Function = new OpenAiFunctionCallWire { Name = tc.Name, Arguments = tc.WireArgumentsJson }
                })]
                : null
        };
    }

    private static OpenAiToolWire ToWireTool(AiToolDefinition t)
    {
        return new()
        {
            Type = "function",
            Function = new OpenAiFunctionDefWire
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = JsonDocument.Parse(t.ParametersSchemaJson).RootElement.Clone()
            }
        };
    }

    private sealed class ToolCallAccumulator
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Arguments = new();
    }
}
