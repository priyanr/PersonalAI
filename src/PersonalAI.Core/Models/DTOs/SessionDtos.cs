using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.DTOs;

public record SessionSummaryDto(
    Guid Id,
    string? Title,
    string ProviderKey,
    string ModelName,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record ToolFlags(
    bool WebSearchEnabled,
    bool ReadFileEnabled,
    bool WriteFileEnabled,
    bool EditFileEnabled,
    bool ListFilesEnabled,
    bool SearchFilesEnabled,
    bool NetworkScanEnabled,
    bool HttpRequestEnabled,
    bool ComputerControlEnabled,
    bool SandboxEnabled,
    bool GmailEnabled = false)
{
    public static readonly ToolFlags Default = new(true, true, true, true, true, true, false, true, false, false, false);
}

public record SessionDetailDto(
    Guid Id,
    string? Title,
    string ProviderKey,
    string ModelName,
    string? SystemPrompt,
    SessionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool ToolsEnabled,
    int? NumCtx,
    bool TrimHistoryEnabled,
    bool? ThinkingEnabled,
    string? ReasoningEffort,
    int? MaxLlmCalls,
    int TotalLlmCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    IReadOnlyList<Guid> EnabledSkillIds,
    ToolFlags ToolFlags,
    IReadOnlyList<MessageDto> Messages,
    IReadOnlyList<ToolInvocationDto> PendingApprovals,
    // True while a turn is running in the background (ChatTurnRunner) — a reloaded page uses it to
    // re-attach to GET /api/sessions/{id}/turn/stream instead of offering a fresh send.
    bool IsTurnRunning);

public record CreateSessionRequest(
    string? Title,
    string ModelName,
    string ProviderKey = "ollama",
    bool ToolsEnabled = true,
    int? NumCtx = null,
    bool TrimHistoryEnabled = true,
    bool? ThinkingEnabled = null,
    int? MaxLlmCalls = null,
    IReadOnlyList<Guid>? EnabledSkillIds = null,
    ToolFlags? ToolFlags = null,
    string? ReasoningEffort = null);

public record SessionPageDto(IReadOnlyList<SessionSummaryDto> Items, int TotalCount, int Page, int PageSize);

public record RenameSessionRequest(string? Title);

// ProviderKey / ModelName are optional: null keeps the session's current model, so older clients that only
// send tool / behaviour settings are unaffected.
public record UpdateSessionSettingsRequest(bool ToolsEnabled, int? NumCtx, bool TrimHistoryEnabled, bool? ThinkingEnabled, int? MaxLlmCalls, IReadOnlyList<Guid>? EnabledSkillIds, ToolFlags ToolFlags, string? ReasoningEffort = null, string? ProviderKey = null, string? ModelName = null);

public record SessionSettingsDto(Guid Id, string ProviderKey, string ModelName, bool ToolsEnabled, int? NumCtx, bool TrimHistoryEnabled, bool? ThinkingEnabled, string? ReasoningEffort, int? MaxLlmCalls, IReadOnlyList<Guid> EnabledSkillIds, ToolFlags ToolFlags);
