using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Core.Services.Sessions;

/// <summary>Chat sessions (/api/sessions and /api/tool-invocations): list, create, rename, change settings, and start the
/// turns ChatTurnRunner runs in the background. The turn itself is ChatAgentService.</summary>
public class ChatSessionsService(AppDbContext db, IChatSessionRepository repo, ChatTurnRunner turns, AIProviderRegistry providers)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxTitleLength = 200;

    private const string TurnRunningMessage = "A response is already being generated in this chat. Stop it or wait for it to finish.";

    public async Task<SessionPageDto> GetSessions(string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, totalCount) = await repo.GetSessionsAsync(search, page, pageSize, ct);
        return new SessionPageDto(items.Select(ToSummaryDto).ToList(), totalCount, page, pageSize);
    }

    public async Task<SessionDetailDto> GetSession(Guid id, CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(id, ct) ?? throw NotFound(id);
        return ToDetailDto(session, turns.GetActive(id) is not null);
    }

    public async Task<SessionSummaryDto> Create(CreateSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModelName)) throw new InvalidRequestException("modelName is required.");
        var reasoningEffort = NormalizeReasoningEffort(request.ReasoningEffort);

        var session = new ChatSession
        {
            Title = request.Title,
            ProviderKey = request.ProviderKey,
            ModelName = request.ModelName,
            ToolsEnabled = request.ToolsEnabled,
            NumCtx = request.NumCtx,
            TrimHistoryEnabled = request.TrimHistoryEnabled,
            ThinkingEnabled = request.ThinkingEnabled,
            ReasoningEffort = reasoningEffort,
            MaxLlmCalls = request.MaxLlmCalls,
            EnabledSkillIdsJson = JsonSerializer.Serialize(request.EnabledSkillIds ?? [])
        };
        ApplyToolFlags(session, request.ToolFlags ?? ToolFlags.Default);
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return ToSummaryDto(session);
    }

    public async Task<SessionSummaryDto> Rename(Guid id, RenameSessionRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title)) throw new InvalidRequestException("title is required.");
        if (title.Length > MaxTitleLength) throw new InvalidRequestException($"title must be at most {MaxTitleLength} characters.");

        var session = await repo.GetSessionWithoutMessagesAsync(id, ct) ?? throw NotFound(id);
        session.Title = title;
        await db.SaveChangesAsync(ct);
        return ToSummaryDto(session);
    }

    public async Task<SessionSettingsDto> UpdateSettings(Guid id, UpdateSessionSettingsRequest request, CancellationToken ct)
    {
        var reasoningEffort = NormalizeReasoningEffort(request.ReasoningEffort);
        var providerKey = request.ProviderKey?.Trim();
        var modelName = request.ModelName?.Trim();
        if (providerKey is not null && !providers.TryResolve(providerKey, out _))
            throw new InvalidRequestException($"Unknown AI provider '{providerKey}'.");
        if (modelName is not null && modelName.Length == 0)
            throw new InvalidRequestException("modelName cannot be empty.");
        // Switching provider without naming a model would pair the old model name with a provider that likely
        // doesn't have it.
        if (providerKey is not null && modelName is null)
            throw new InvalidRequestException("modelName is required when changing providerKey.");

        // Clients send the model only when changing it, so any model here during a running turn is a mid-reply switch.
        if (modelName is not null && turns.GetActive(id) is not null)
            throw new ConflictException("A response is being generated in this chat. Stop it or wait for it to finish before switching models.");

        var session = await repo.GetSessionWithoutMessagesAsync(id, ct) ?? throw NotFound(id);
        if (providerKey is not null) session.ProviderKey = providerKey;
        if (modelName is not null) session.ModelName = modelName;
        session.ToolsEnabled = request.ToolsEnabled;
        session.NumCtx = request.NumCtx;
        session.TrimHistoryEnabled = request.TrimHistoryEnabled;
        session.ThinkingEnabled = request.ThinkingEnabled;
        session.ReasoningEffort = reasoningEffort;
        session.MaxLlmCalls = request.MaxLlmCalls;
        session.EnabledSkillIdsJson = JsonSerializer.Serialize(request.EnabledSkillIds ?? []);
        ApplyToolFlags(session, request.ToolFlags);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return new SessionSettingsDto(session.Id, session.ProviderKey, session.ModelName, session.ToolsEnabled, session.NumCtx,
            session.TrimHistoryEnabled, session.ThinkingEnabled, session.ReasoningEffort, session.MaxLlmCalls, ToEnabledSkillIds(session), ToToolFlags(session));
    }

    public async Task<ChatTurn> StartMessage(Guid id, string text, CancellationToken ct)
    {
        if (!await repo.SessionExistsAsync(id, ct)) throw NotFound(id);
        return turns.TryStart(id, (agent, turnCt) => agent.RunTurnAsync(id, text, turnCt))
            ?? throw new ConflictException(TurnRunningMessage);
    }

    /// <summary>Compaction runs as a turn, so it can't overlap a response being generated and can be stopped or re-attached like one.</summary>
    public async Task<ChatTurn> StartCompaction(Guid id, CancellationToken ct)
    {
        if (!await repo.SessionExistsAsync(id, ct)) throw NotFound(id);
        if (await repo.HasPendingApprovalsAsync(id, ct))
            throw new ConflictException("Resolve the pending tool approvals before compacting this chat.");
        return turns.TryStart(id, (agent, turnCt) => agent.CompactHistoryAsync(id, turnCt))
            ?? throw new ConflictException(TurnRunningMessage);
    }

    public async Task<ChatTurn> StartApprovalResolution(Guid toolInvocationId, bool approved, CancellationToken ct)
    {
        var invocation = await repo.GetToolInvocationAsync(toolInvocationId, ct)
            ?? throw new NotFoundException($"Tool invocation {toolInvocationId} not found.");
        if (invocation.Status != ToolInvocationStatus.PendingApproval)
            throw new ConflictException($"Tool invocation is not pending approval (status: {invocation.Status}).");

        return turns.TryStart(invocation.SessionId, (agent, turnCt) => agent.ResolveApprovalAsync(toolInvocationId, approved, turnCt))
            ?? throw new ConflictException("A response is already being generated in this chat. Wait for it to finish.");
    }

    private static string? NormalizeReasoningEffort(string? value)
    {
        return ModelSettings.TryNormalizeReasoningEffort(value, out var normalized)
            ? normalized
            : throw new InvalidRequestException(ModelSettings.InvalidReasoningEffortMessage(value));
    }

    private static NotFoundException NotFound(Guid id)
    {
        return new NotFoundException($"Session {id} not found.");
    }

    private static void ApplyToolFlags(ChatSession session, ToolFlags flags)
    {
        session.WebSearchEnabled = flags.WebSearchEnabled;
        session.ReadFileEnabled = flags.ReadFileEnabled;
        session.WriteFileEnabled = flags.WriteFileEnabled;
        session.EditFileEnabled = flags.EditFileEnabled;
        session.ListFilesEnabled = flags.ListFilesEnabled;
        session.SearchFilesEnabled = flags.SearchFilesEnabled;
        session.NetworkScanEnabled = flags.NetworkScanEnabled;
        session.HttpRequestEnabled = flags.HttpRequestEnabled;
        session.ComputerControlEnabled = flags.ComputerControlEnabled;
        session.SandboxEnabled = flags.SandboxEnabled;
        session.GmailEnabled = flags.GmailEnabled;
    }

    private static ToolFlags ToToolFlags(ChatSession s)
    {
        return new ToolFlags(
            s.WebSearchEnabled, s.ReadFileEnabled, s.WriteFileEnabled, s.EditFileEnabled, s.ListFilesEnabled, s.SearchFilesEnabled,
            s.NetworkScanEnabled, s.HttpRequestEnabled, s.ComputerControlEnabled, s.SandboxEnabled, s.GmailEnabled);
    }

    private static List<Guid> ToEnabledSkillIds(ChatSession s)
    {
        return JsonSerializer.Deserialize<List<Guid>>(s.EnabledSkillIdsJson) ?? [];
    }

    private static SessionSummaryDto ToSummaryDto(ChatSession s)
    {
        return new SessionSummaryDto(s.Id, s.Title, s.ProviderKey, s.ModelName, s.Status, s.CreatedAt, s.UpdatedAt);
    }

    private static SessionDetailDto ToDetailDto(ChatSession s, bool isTurnRunning)
    {
        return new SessionDetailDto(
            s.Id, s.Title, s.ProviderKey, s.ModelName, s.SystemPrompt, s.Status, s.CreatedAt, s.UpdatedAt,
            s.ToolsEnabled, s.NumCtx, s.TrimHistoryEnabled, s.ThinkingEnabled, s.ReasoningEffort, s.MaxLlmCalls,
            s.TotalLlmCalls, s.TotalInputTokens, s.TotalOutputTokens, ToEnabledSkillIds(s), ToToolFlags(s),
            s.Messages.Select(m => new MessageDto(
                m.Id, m.SequenceNumber, m.Role, m.Content,
                string.IsNullOrEmpty(m.ToolCallsJson) ? null : JsonSerializer.Deserialize<List<ToolCallDto>>(m.ToolCallsJson),
                m.ToolCallId, m.ToolName, m.IsError, m.CreatedAt)).ToList(),
            s.ToolInvocations
                .Where(t => t.Status == ToolInvocationStatus.PendingApproval)
                .Select(t => new ToolInvocationDto(t.Id, t.ToolName, t.ArgumentsJson, t.Status, t.ResolvedPath, t.Reason, t.ResultContent, t.IsError))
                .ToList(),
            isTurnRunning);
    }
}
