using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.AI.Ollama;

public sealed class OllamaProvider(HttpClient httpClient, AIProviderConfigCache configCache, IOptions<AgentSettings> agentSettings) : IAIProvider
{
    public const string ProviderKey = "ollama";
    public const string DefaultBaseUrl = "http://localhost:11434";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string Key => ProviderKey;
    public string DisplayName => "Ollama (local)";
    public bool SupportsThinkingToggle => true;

    public IReadOnlyList<ProviderConfigField> ModelSettingFields => ModelSettings.OllamaFields;

    public IReadOnlyList<ProviderConfigField> ConfigFields { get; } =
    [
        new ProviderConfigField("BaseUrl", "Base URL", ProviderConfigFieldType.Url, Required: true, DefaultValue: DefaultBaseUrl),
        AIProviderConfigCache.MaxOutputTokensConfigField
    ];

    private string ResolveBaseUrl()
    {
        return (configCache.GetValue(ProviderKey, "BaseUrl") ?? DefaultBaseUrl).TrimEnd('/');
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
        var baseUrl = ResolveBaseUrl();
        var requestBody = new OllamaChatRequestWire
        {
            Model = model,
            Stream = true,
            Messages = [.. messages.Select(ToWireMessage)],
            Tools = tools.Count == 0 || configCache.ToolsDisabled(ProviderKey, model) ? null : [.. tools.Select(ToWireTool)],
            Options = new OllamaOptionsWire
            {
                NumCtx = numCtx is > 0 ? numCtx : null,
                NumPredict = configCache.ResolveMaxOutputTokens(ProviderKey, model, agentSettings.Value.MaxOutputTokens)
            },
            Think = ResolveThink(configCache.ResolveThink(ProviderKey, model, think), reasoningEffort)
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaChatChunkWire? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaChatChunkWire>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk is null) continue;

            var toolCalls = chunk.Message?.ToolCalls is { Count: > 0 }
                ? chunk.Message.ToolCalls.Select((tc, i) => new AiToolCall(
                    ToolCallId: Guid.NewGuid().ToString("N"),
                    Name: tc.Function?.Name ?? "",
                    ArgumentsJson: tc.Function is { } f && f.Arguments.ValueKind != JsonValueKind.Undefined
                        ? f.Arguments.GetRawText()
                        : "{}")).ToList()
                : null;

            var usage = chunk.Done && (chunk.PromptEvalCount is not null || chunk.EvalCount is not null)
                ? new AiTokenUsage(chunk.PromptEvalCount ?? 0, chunk.EvalCount ?? 0)
                : null;

            yield return new AiChatChunk(
                chunk.Message?.Content, toolCalls, chunk.Done, usage,
                ReasoningDelta: chunk.Message?.Thinking,
                HitOutputLimit: chunk.Done && chunk.DoneReason == "length");

            if (chunk.Done) yield break;
        }
    }

    /// <summary>Folds the thinking switch and the reasoning effort into Ollama's single "think" field. Thinking
    /// switched off (by the chat or the model's pin) wins; effort "none" turns it off; any other effort asks for
    /// that level (Ollama only has low/medium/high), which also turns thinking on for an agent run that has no
    /// thinking preference of its own. No preference and no effort means off: Ollama's own default would turn
    /// thinking on for every thinking-capable model in skill runs and compaction.</summary>
    private static object ResolveThink(bool? think, string? reasoningEffort)
    {
        if (think == false || reasoningEffort is null)
            return think ?? false;
        return reasoningEffort.ToLowerInvariant() switch
        {
            "none" => false,
            "minimal" or "low" => "low",
            "medium" => "medium",
            _ => "high"
        };
    }

    public async Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl();
        var response = await httpClient.GetFromJsonAsync<OllamaTagsResponseWire>($"{baseUrl}/api/tags", JsonOptions, ct);

        return response?.Models?.Select(m => new AiModelInfo(
            m.Name,
            m.Size,
            m.Digest,
            m.ModifiedAt,
            m.Details?.ParameterSize,
            m.Details?.QuantizationLevel)).ToList() ?? [];
    }

    public async IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl();
        var requestBody = new OllamaPullRequestWire { Name = modelName, Stream = true };
        var json = JsonSerializer.Serialize(requestBody, JsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/pull")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaPullProgressWire? progress;
            try
            {
                progress = JsonSerializer.Deserialize<OllamaPullProgressWire>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (progress is null) continue;
            yield return new AiPullProgress(progress.Status, progress.Digest, progress.Total, progress.Completed);

            if (progress.Status is "success") yield break;
        }
    }

    private static OllamaMessageWire ToWireMessage(AiChatMessage m)
    {
        return new()
        {
            Role = m.Role,
            Content = m.Content ?? "",
            ToolCalls = m.ToolCalls is { Count: > 0 }
                ? [.. m.ToolCalls.Select(tc => new OllamaToolCallWire
                {
                    Function = new OllamaFunctionCallWire
                    {
                        Name = tc.Name,
                        Arguments = JsonDocument.Parse(tc.WireArgumentsJson).RootElement.Clone()
                    }
                })]
                : null
        };
    }

    private static OllamaToolWire ToWireTool(AiToolDefinition t)
    {
        return new()
        {
            Type = "function",
            Function = new OllamaFunctionDefWire
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = JsonDocument.Parse(t.ParametersSchemaJson).RootElement.Clone()
            }
        };
    }
}
