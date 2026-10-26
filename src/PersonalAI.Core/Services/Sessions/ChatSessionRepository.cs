using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Sessions;

public interface IChatSessionRepository
{
    /// <summary>One page (1-based) of sessions, newest UpdatedAt first, optionally filtered by a
    /// case-insensitive title substring. TotalCount is the filtered count before paging.</summary>
    Task<(List<ChatSession> Items, int TotalCount)> GetSessionsAsync(string? search, int page, int pageSize, CancellationToken ct);

    /// <summary>Loads a session with its Messages (ordered by SequenceNumber) and ToolInvocations.</summary>
    Task<ChatSession?> GetSessionAsync(Guid sessionId, CancellationToken ct);

    /// <summary>The session row alone, for changing its settings or title without loading the transcript.</summary>
    Task<ChatSession?> GetSessionWithoutMessagesAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Existence check only. GetSessionAsync eagerly loads every message and tool invocation,
    /// which is far too much work for callers that only need to know whether the row is still there —
    /// notably the sandbox reaper, which asks this about every live sandbox on every poll.</summary>
    Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct);

    Task<List<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken ct);

    Task<ToolInvocation?> GetToolInvocationAsync(Guid toolInvocationId, CancellationToken ct);
    Task<List<ToolInvocation>> GetPendingApprovalsAsync(Guid requestingMessageId, CancellationToken ct);
    Task<bool> HasPendingApprovalsAsync(Guid sessionId, CancellationToken ct);
}

public class ChatSessionRepository(AppDbContext db) : IChatSessionRepository
{
    public async Task<(List<ChatSession> Items, int TotalCount)> GetSessionsAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<ChatSession> query = db.ChatSessions.AsNoTracking();
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

    public async Task<ChatSession?> GetSessionAsync(Guid sessionId, CancellationToken ct)
    {
        return await db.ChatSessions
            .Include(s => s.Messages.OrderBy(m => m.SequenceNumber))
            .Include(s => s.ToolInvocations)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
    }

    public async Task<ChatSession?> GetSessionWithoutMessagesAsync(Guid sessionId, CancellationToken ct)
    {
        return await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
    }

    public async Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken ct)
    {
        return await db.ChatSessions.AnyAsync(s => s.Id == sessionId, ct);
    }

    public async Task<List<ChatMessage>> GetMessagesAsync(Guid sessionId, CancellationToken ct)
    {
        return await db.ChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync(ct);
    }

    public async Task<ToolInvocation?> GetToolInvocationAsync(Guid toolInvocationId, CancellationToken ct)
    {
        return await db.ToolInvocations.FirstOrDefaultAsync(t => t.Id == toolInvocationId, ct);
    }

    public async Task<List<ToolInvocation>> GetPendingApprovalsAsync(Guid requestingMessageId, CancellationToken ct)
    {
        return await db.ToolInvocations
            .Where(t => t.RequestingMessageId == requestingMessageId && t.Status == ToolInvocationStatus.PendingApproval)
            .ToListAsync(ct);
    }

    public async Task<bool> HasPendingApprovalsAsync(Guid sessionId, CancellationToken ct)
    {
        return await db.ToolInvocations.AnyAsync(
            t => t.SessionId == sessionId && t.Status == ToolInvocationStatus.PendingApproval, ct);
    }
}
