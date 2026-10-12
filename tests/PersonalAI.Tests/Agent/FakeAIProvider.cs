using System.Runtime.CompilerServices;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;

namespace PersonalAI.Tests.Agent;

/// <summary>Replays one canned chunk sequence per call to ChatStreamAsync, in order, and records
/// the wire messages each call was given so tests can assert on what history was sent back.</summary>
public sealed class FakeAIProvider(params IReadOnlyList<AiChatChunk>[] responses) : IAIProvider
{
    private readonly Queue<IReadOnlyList<AiChatChunk>> _responses = new(responses);

    public string Key => "ollama";
    public string DisplayName => "Fake";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];

    public List<IReadOnlyList<AiChatMessage>> RecordedRequests { get; } = [];
    public List<IReadOnlyList<AiToolDefinition>> RecordedToolSets { get; } = [];
    public List<int?> RecordedNumCtx { get; } = [];
    public List<bool?> RecordedThink { get; } = [];
    public List<string?> RecordedReasoningEffort { get; } = [];

    /// <summary>When set, the next call to ChatStreamAsync throws this instead of dequeuing a canned
    /// response (and is cleared afterward) — lets tests exercise a provider failure, e.g. mid-turn
    /// history compaction, without consuming a slot in the canned response queue.</summary>
    public Exception? ThrowOnNextCall { get; set; }

    public async IAsyncEnumerable<AiChatChunk> ChatStreamAsync(
        string model,
        IReadOnlyList<AiChatMessage> messages,
        IReadOnlyList<AiToolDefinition> tools,
        int? numCtx,
        bool? think,
        string? reasoningEffort,
        [EnumeratorCancellation] CancellationToken ct)
    {
        RecordedRequests.Add(messages);
        RecordedToolSets.Add(tools);
        RecordedNumCtx.Add(numCtx);
        RecordedThink.Add(think);
        RecordedReasoningEffort.Add(reasoningEffort);

        if (ThrowOnNextCall is { } ex)
        {
            ThrowOnNextCall = null;
            throw ex;
        }

        if (!_responses.TryDequeue(out var chunks))
            throw new InvalidOperationException("FakeAIProvider ran out of canned responses.");

        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) =>
        throw new NotSupportedException("Not used by ChatAgentService.");

    public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) =>
        throw new NotSupportedException("Not used by ChatAgentService.");
}
