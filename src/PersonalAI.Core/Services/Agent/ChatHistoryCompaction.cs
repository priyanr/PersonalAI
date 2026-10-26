using System.Text;
using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.AI;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.Agent;

/// <summary>Keeps a long chat inside the model's context window: old messages are folded into the session's
/// HistorySummary by a side LLM call, and the model is sent the summary plus the newer messages. The raw messages
/// stay in the transcript. See COMPACTION_ARCHITECTURE.md.</summary>
public sealed class ChatHistoryCompaction(AppDbContext db, AIProviderRegistry providers, IOptions<AgentSettings> agentSettings)
{
    private AgentSettings Settings => agentSettings.Value;

    /// <summary>The history the model sees this call. A summary (or, when compaction failed this turn, a note about
    /// dropped messages) is added to <paramref name="wire"/>; the returned messages are the ones still sent raw.</summary>
    public IReadOnlyList<ChatMessage> VisibleHistory(
        ChatSession session, IReadOnlyList<ChatMessage> history, bool compactionFailed, List<AiChatMessage> wire)
    {
        if (session.TrimHistoryEnabled && compactionFailed)
        {
            // The summary call failed: drop old messages rather than send a stale summary.
            var start = KeepWindowStartIndex(history, Settings.HistoryKeepRecentMessages);
            if (start == 0) return history;
            wire.Add(new AiChatMessage("system", $"[{start} earlier message(s) omitted to fit the model's context window.]"));
            return history.Skip(start).ToList();
        }

        // Also without automatic compaction: a summary from a manual compaction still replaces what it covers.
        if (session.SummarizedThroughSequenceNumber <= 0 || string.IsNullOrEmpty(session.HistorySummary))
            return history;
        wire.Add(new AiChatMessage("system", $"[Summary of earlier conversation]\n{session.HistorySummary}"));
        return history.Where(m => m.SequenceNumber > session.SummarizedThroughSequenceNumber).ToList();
    }

    /// <summary>Automatic compaction: folds unsummarized history older than the keep window once either trigger is
    /// crossed (CompactionBatchSize messages or CompactionMaxCharsPerCall characters). False when the summary call
    /// failed, so the turn falls back to dropping old messages.</summary>
    public async Task<bool> CompactIfDueAsync(ChatSession session, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var keepWindowStart = KeepWindowStartIndex(history, Settings.HistoryKeepRecentMessages);
        if (keepWindowStart == 0) return true;

        var pending = history.Take(keepWindowStart).Where(m => m.SequenceNumber > session.SummarizedThroughSequenceNumber).ToList();
        if (pending.Count == 0) return true;
        if (pending.Count < Settings.CompactionBatchSize && pending.Sum(EffectiveLength) < Settings.CompactionMaxCharsPerCall)
            return true;

        return (await FoldIntoSummaryAsync(session, pending, ct)).Folded > 0;
    }

    /// <summary>Folds the oldest of <paramref name="pending"/> (up to CompactionMaxCharsPerCall characters, never
    /// separating a tool call from its result) into the summary with one provider call, and saves it. Folded is 0 when
    /// the call failed or returned nothing; Totals are the session's new token totals when the provider reported usage.</summary>
    public async Task<(int Folded, (long InputTokens, long OutputTokens)? Totals)> FoldIntoSummaryAsync(
        ChatSession session, IReadOnlyList<ChatMessage> pending, CancellationToken ct)
    {
        var toFold = new List<ChatMessage>();
        var used = 0;
        foreach (var m in pending)
        {
            var length = EffectiveLength(m);
            // At least one message is always folded, so a single oversized message can't stall compaction.
            if (toFold.Count > 0 && used + length > Settings.CompactionMaxCharsPerCall) break;
            toFold.Add(m);
            used += length;
        }
        // Never leave a tool result raw while its call is summarized: extend forward to include it. Shrinking back
        // instead could defer the fold forever, since pending is rescanned from the same start every pass.
        var count = toFold.Count;
        while (count < pending.Count && pending[count].Role == MessageRole.Tool) count++;
        toFold = pending.Take(count).ToList();

        var prompt = new List<AiChatMessage>
        {
            new("system",
                "You maintain a compact, factual running summary of an ongoing agent conversation. Given " +
                "the previous summary (if any) and a batch of new messages, produce ONE updated summary as " +
                "terse bullet points. Preserve: the user's goals/requests, decisions made, established " +
                "facts, file paths touched, and tool-call outcomes (success/failure plus the key result). " +
                "Do not write prose or a narrative recap — bullets only. Merge the new messages into the " +
                "previous summary rather than appending a separate section."),
            new("user", $"Previous summary:\n{session.HistorySummary ?? "(none)"}\n\nMessages to fold in:\n{FormatForSummary(toFold)}")
        };

        var summary = new StringBuilder();
        AiTokenUsage? usage = null;
        try
        {
            await foreach (var chunk in providers.Resolve(session.ProviderKey).ChatStreamAsync(
                session.ModelName, prompt, [], session.NumCtx, think: null, reasoningEffort: null, ct))
            {
                if (!string.IsNullOrEmpty(chunk.ContentDelta)) summary.Append(chunk.ContentDelta);
                if (chunk.Usage is not null) usage = chunk.Usage;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return (0, null);
        }

        // A real provider cost even though it isn't one of the turn's counted LLM calls.
        (long, long)? totals = null;
        if (usage is not null)
        {
            session.TotalInputTokens += usage.InputTokens;
            session.TotalOutputTokens += usage.OutputTokens;
            await db.SaveChangesAsync(ct);
            totals = (session.TotalInputTokens, session.TotalOutputTokens);
        }

        var newSummary = summary.ToString().Trim();
        if (newSummary.Length == 0) return (0, totals);

        // Every pass feeds the whole previous summary back in, so a model that compresses poorly could grow it until it
        // alone fills the context window. Cap it: losing the oldest bullets beats an overflow.
        var max = Settings.CompactionMaxSummaryChars;
        if (newSummary.Length > max)
            newSummary = newSummary[..max] + $"\n...[earlier summary truncated, {newSummary.Length - max} characters dropped]";

        session.HistorySummary = newSummary;
        session.SummarizedThroughSequenceNumber = toFold[^1].SequenceNumber;
        await db.SaveChangesAsync(ct);
        return (toFold.Count, totals);
    }

    internal static string? CapContent(string? content, int maxChars)
    {
        if (content is null || content.Length <= maxChars) return content;
        return content[..maxChars] + $"\n...[{content.Length - maxChars} characters truncated]";
    }

    // Where the most-recent-keep window begins. Never on a Tool message, so a result is never sent without its call.
    private static int KeepWindowStartIndex(IReadOnlyList<ChatMessage> history, int keep)
    {
        if (history.Count <= keep) return 0;

        var start = history.Count - keep;
        while (start < history.Count && history[start].Role == MessageRole.Tool)
            start++;
        return start;
    }

    private int EffectiveLength(ChatMessage m)
    {
        var length = m.Content?.Length ?? 0;
        return m.Role == MessageRole.Tool ? Math.Min(length, Settings.ToolResultMaxChars) : length;
    }

    private string FormatForSummary(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages.Where(m => !m.ExcludeFromContext))
        {
            var label = m.Role == MessageRole.Tool && !string.IsNullOrEmpty(m.ToolName) ? $"tool:{m.ToolName}" : m.Role.ToString().ToLowerInvariant();
            var content = m.Role == MessageRole.Tool ? CapContent(m.Content, Settings.ToolResultMaxChars) : m.Content;

            // A tool-calling Assistant message usually has no text; without its calls the summary couldn't say what a
            // tool was used for.
            if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson))
            {
                var calls = DescribeToolCalls(m.ToolCallsJson);
                if (calls.Length > 0) content = string.IsNullOrEmpty(content) ? calls : $"{content}\n{calls}";
            }

            sb.Append(label).Append(": ").AppendLine(content);
        }
        return sb.ToString();
    }

    private static string DescribeToolCalls(string toolCallsJson)
    {
        List<AiToolCall>? calls;
        try
        {
            calls = JsonSerializer.Deserialize<List<AiToolCall>>(toolCallsJson);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        return calls is not { Count: > 0 } ? string.Empty : string.Join("\n", calls.Select(c => $"[called {c.Name}({c.ArgumentsJson})]"));
    }
}
