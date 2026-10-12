using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Interfaces;

public interface IChatSessionRepository
{
    Task<ChatSession> CreateSessionAsync(
        string? title,
        string modelName,
        CancellationToken ct,
        string providerKey = "ollama",
        bool toolsEnabled = true,
        int? numCtx = null,
        bool trimHistoryEnabled = true,
        bool? thinkingEnabled = null,
        int? maxLlmCalls = null,
        IReadOnlyList<Guid>? enabledSkillIds = null,
        ToolFlags? toolFlags = null,
        string? reasoningEffort = null);

    /// <summary>One page (1-based) of sessions, newest UpdatedAt first, optionally filtered by a
    /// case-insensitive title substring. TotalCount is the filtered count before paging.</summary>
    Task<(IReadOnlyList<ChatSession> Items, int TotalCount)> GetSessionsAsync(string? search, int page, int pageSize, CancellationToken ct);

    /// <summary>Loads a session with its Messages (ordered by SequenceNumber) and ToolInvocations.</summary>
    Task<ChatSession?> GetSessionAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Existence check only. GetSessionAsync eagerly loads every message and tool invocation,
    /// which is far too much work for callers that only need to know whether the row is still there —
    /// notably the sandbox reaper, which asks this about every live sandbox on every poll.</summary>
    Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct);

    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Assigns SequenceNumber as max+1 for the session and persists the message.</summary>
    Task<ChatMessage> AppendMessageAsync(ChatMessage message, CancellationToken ct);

    Task SetSessionStatusAsync(Guid sessionId, SessionStatus status, CancellationToken ct);
    /// <summary>Returns the updated session, or null when it doesn't exist.</summary>
    Task<ChatSession?> SetSessionTitleAsync(Guid sessionId, string title, CancellationToken ct);
    Task<ChatSession?> UpdateSessionSettingsAsync(Guid sessionId, bool toolsEnabled, int? numCtx, bool trimHistoryEnabled, bool? thinkingEnabled, string? reasoningEffort, int? maxLlmCalls, IReadOnlyList<Guid> enabledSkillIds, ToolFlags toolFlags, CancellationToken ct, string? providerKey = null, string? modelName = null);

    /// <summary>Records that the session is about to make another LLM call; returns the session's new lifetime total.</summary>
    Task<int> IncrementLlmCallsAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Adds one provider call's token usage to the session's totals; returns the new totals.</summary>
    Task<(long InputTokens, long OutputTokens)> AddTokenUsageAsync(Guid sessionId, AiTokenUsage usage, CancellationToken ct);

    /// <summary>Persists a compaction pass's result: the updated running summary and how far its cursor
    /// now reaches. See COMPACTION_ARCHITECTURE.md.</summary>
    Task UpdateHistoryCompactionAsync(Guid sessionId, string historySummary, int summarizedThroughSequenceNumber, CancellationToken ct);

    Task<ToolInvocation> CreateToolInvocationAsync(ToolInvocation invocation, CancellationToken ct);
    Task<ToolInvocation?> GetToolInvocationAsync(Guid toolInvocationId, CancellationToken ct);
    Task UpdateToolInvocationAsync(ToolInvocation invocation, CancellationToken ct);
    Task<IReadOnlyList<ToolInvocation>> GetPendingApprovalsAsync(Guid requestingMessageId, CancellationToken ct);
    Task<bool> HasPendingApprovalsAsync(Guid sessionId, CancellationToken ct);
}
