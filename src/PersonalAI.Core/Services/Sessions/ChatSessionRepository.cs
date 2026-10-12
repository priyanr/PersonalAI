using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Sessions;

public class ChatSessionRepository(AppDbContext db) : IChatSessionRepository
{
    public async Task<ChatSession> CreateSessionAsync(
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
        string? reasoningEffort = null)
    {
        var flags = toolFlags ?? ToolFlags.Default;
        var session = new ChatSession
        {
            Title = title,
            ProviderKey = providerKey,
            ModelName = modelName,
            ToolsEnabled = toolsEnabled,
            NumCtx = numCtx,
            TrimHistoryEnabled = trimHistoryEnabled,
            ThinkingEnabled = thinkingEnabled,
            ReasoningEffort = reasoningEffort,
            MaxLlmCalls = maxLlmCalls,
            EnabledSkillIdsJson = JsonSerializer.Serialize(enabledSkillIds ?? []),
            WebSearchEnabled = flags.WebSearchEnabled,
            ReadFileEnabled = flags.ReadFileEnabled,
            WriteFileEnabled = flags.WriteFileEnabled,
            EditFileEnabled = flags.EditFileEnabled,
            ListFilesEnabled = flags.ListFilesEnabled,
            SearchFilesEnabled = flags.SearchFilesEnabled,
            NetworkScanEnabled = flags.NetworkScanEnabled,
            HttpRequestEnabled = flags.HttpRequestEnabled,
            ComputerControlEnabled = flags.ComputerControlEnabled,
            SandboxEnabled = flags.SandboxEnabled,
            GmailEnabled = flags.GmailEnabled
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<(IReadOnlyList<ChatSession> Items, int TotalCount)> GetSessionsAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<ChatSession> query = db.ChatSessions;
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ToLower on both sides rather than EF.Functions.Like: no wildcard escaping to get wrong,
            // and it behaves the same on SQLite and the in-memory test provider.
            var term = search.Trim().ToLower();
            query = query.Where(s => s.Title != null && s.Title.ToLower().Contains(term));
        }

        // Ordered and paged client-side: SQLite cannot translate ORDER BY over DateTimeOffset, and session
        // counts for a local single-user app are small enough that this is not a concern.
        var sessions = await query.ToListAsync(ct);
        var items = sessions
            .OrderByDescending(s => s.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return (items, sessions.Count);
    }

    public async Task<ChatSession?> GetSessionAsync(Guid sessionId, CancellationToken ct) =>
        await db.ChatSessions
            .Include(s => s.Messages.OrderBy(m => m.SequenceNumber))
            .Include(s => s.ToolInvocations)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public async Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct) =>
        await db.ChatSessions.AnyAsync(s => s.Id == sessionId, ct);

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken ct) =>
        await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync(ct);

    public async Task<ChatMessage> AppendMessageAsync(ChatMessage message, CancellationToken ct)
    {
        var maxSequence = await db.ChatMessages
            .Where(m => m.SessionId == message.SessionId)
            .Select(m => (int?)m.SequenceNumber)
            .MaxAsync(ct) ?? 0;

        message.SequenceNumber = maxSequence + 1;
        db.ChatMessages.Add(message);

        var session = await db.ChatSessions.FindAsync([message.SessionId], ct);
        if (session is not null)
            session.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task SetSessionStatusAsync(Guid sessionId, SessionStatus status, CancellationToken ct)
    {
        var session = await db.ChatSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        session.Status = status;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ChatSession?> SetSessionTitleAsync(Guid sessionId, string title, CancellationToken ct)
    {
        var session = await db.ChatSessions.FindAsync([sessionId], ct);
        if (session is null) return null;

        session.Title = title;
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<int> IncrementLlmCallsAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.ChatSessions.FindAsync([sessionId], ct);
        if (session is null) return 0;

        session.TotalLlmCalls++;
        await db.SaveChangesAsync(ct);
        return session.TotalLlmCalls;
    }

    public async Task<(long InputTokens, long OutputTokens)> AddTokenUsageAsync(Guid sessionId, AiTokenUsage usage, CancellationToken ct)
    {
        var session = await db.ChatSessions.FindAsync([sessionId], ct);
        if (session is null) return (0, 0);

        session.TotalInputTokens += usage.InputTokens;
        session.TotalOutputTokens += usage.OutputTokens;
        await db.SaveChangesAsync(ct);
        return (session.TotalInputTokens, session.TotalOutputTokens);
    }

    public async Task UpdateHistoryCompactionAsync(Guid sessionId, string historySummary, int summarizedThroughSequenceNumber, CancellationToken ct)
    {
        var session = await db.ChatSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        session.HistorySummary = historySummary;
        session.SummarizedThroughSequenceNumber = summarizedThroughSequenceNumber;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ChatSession?> UpdateSessionSettingsAsync(Guid sessionId, bool toolsEnabled, int? numCtx, bool trimHistoryEnabled, bool? thinkingEnabled, string? reasoningEffort, int? maxLlmCalls, IReadOnlyList<Guid> enabledSkillIds, ToolFlags toolFlags, CancellationToken ct, string? providerKey = null, string? modelName = null)
    {
        var session = await db.ChatSessions.FindAsync([sessionId], ct);
        if (session is null) return null;

        if (providerKey is not null) session.ProviderKey = providerKey;
        if (modelName is not null) session.ModelName = modelName;
        session.ToolsEnabled = toolsEnabled;
        session.NumCtx = numCtx;
        session.TrimHistoryEnabled = trimHistoryEnabled;
        session.ThinkingEnabled = thinkingEnabled;
        session.ReasoningEffort = reasoningEffort;
        session.MaxLlmCalls = maxLlmCalls;
        session.EnabledSkillIdsJson = JsonSerializer.Serialize(enabledSkillIds);
        session.WebSearchEnabled = toolFlags.WebSearchEnabled;
        session.ReadFileEnabled = toolFlags.ReadFileEnabled;
        session.WriteFileEnabled = toolFlags.WriteFileEnabled;
        session.EditFileEnabled = toolFlags.EditFileEnabled;
        session.ListFilesEnabled = toolFlags.ListFilesEnabled;
        session.SearchFilesEnabled = toolFlags.SearchFilesEnabled;
        session.NetworkScanEnabled = toolFlags.NetworkScanEnabled;
        session.HttpRequestEnabled = toolFlags.HttpRequestEnabled;
        session.ComputerControlEnabled = toolFlags.ComputerControlEnabled;
        session.SandboxEnabled = toolFlags.SandboxEnabled;
        session.GmailEnabled = toolFlags.GmailEnabled;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<ToolInvocation> CreateToolInvocationAsync(ToolInvocation invocation, CancellationToken ct)
    {
        db.ToolInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
        return invocation;
    }

    public async Task<ToolInvocation?> GetToolInvocationAsync(Guid toolInvocationId, CancellationToken ct) =>
        await db.ToolInvocations.FirstOrDefaultAsync(t => t.Id == toolInvocationId, ct);

    public async Task UpdateToolInvocationAsync(ToolInvocation invocation, CancellationToken ct)
    {
        db.ToolInvocations.Update(invocation);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ToolInvocation>> GetPendingApprovalsAsync(Guid requestingMessageId, CancellationToken ct) =>
        await db.ToolInvocations
            .Where(t => t.RequestingMessageId == requestingMessageId && t.Status == ToolInvocationStatus.PendingApproval)
            .ToListAsync(ct);

    public async Task<bool> HasPendingApprovalsAsync(Guid sessionId, CancellationToken ct) =>
        await db.ToolInvocations.AnyAsync(
            t => t.SessionId == sessionId && t.Status == ToolInvocationStatus.PendingApproval, ct);
}
