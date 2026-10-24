using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Sandbox.Protocol;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Sandbox;

public interface ISandboxRepository
{
    /// <summary>The most recent non-Destroyed sandbox for this owner, if any — a chat session or an
    /// unattended run should have at most one live sandbox at a time.</summary>
    Task<SandboxInstance?> GetActiveByOwnerAsync(Guid ownerId, CancellationToken ct);

    /// <summary>The most recent sandbox for this owner regardless of status — including Destroyed rows —
    /// used only on the cold path where GetActiveByOwnerAsync found nothing, to carry PortsJson forward
    /// across the idle-reaper's Destroyed-but-files-kept boundary and to detect that this owner has used a
    /// sandbox before. Must never feed GetAllActiveAsync/the host-sleep stop-signal decision — those
    /// deliberately still treat Destroyed as gone.</summary>
    Task<SandboxInstance?> GetMostRecentByOwnerAsync(Guid ownerId, CancellationToken ct);

    /// <summary>Chat-session sandboxes (Kind == Session) idle since before <paramref name="sessionCutoff"/>
    /// and ephemeral skill/agent-run sandboxes idle since before <paramref name="ephemeralCutoff"/> — the
    /// two kinds are reaped on different schedules (session containers are retired but their files kept;
    /// ephemeral ones are a hard-kill safety net that deletes files too), so the cutoffs are passed in
    /// separately rather than baked into the query.</summary>
    Task<IReadOnlyList<SandboxInstance>> GetIdleCandidatesAsync(DateTimeOffset sessionCutoff, DateTimeOffset ephemeralCutoff, CancellationToken ct);

    /// <summary>Every sandbox not yet Destroyed — used on broker (re)connect to reconcile the DB's view
    /// against the broker's live container list.</summary>
    Task<IReadOnlyList<SandboxInstance>> GetAllActiveAsync(CancellationToken ct);
}

public class SandboxRepository(AppDbContext db) : ISandboxRepository
{
    public async Task<SandboxInstance?> GetActiveByOwnerAsync(Guid ownerId, CancellationToken ct)
    {
        // Ordered client-side, same rationale as every other repository in this codebase: SQLite can't
        // translate ORDER BY over DateTimeOffset, and per-owner sandbox counts are tiny (at most one
        // live one at a time in practice).
        var candidates = await db.SandboxInstances
            .Where(s => s.OwnerId == ownerId && s.Status != SandboxInstanceStatus.Destroyed)
            .ToListAsync(ct);
        return candidates.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
    }

    public async Task<SandboxInstance?> GetMostRecentByOwnerAsync(Guid ownerId, CancellationToken ct)
    {
        // Same client-side ordering as GetActiveByOwnerAsync and for the same reason — no Status filter
        // here, unlike that method, since the whole point is to also see Destroyed rows.
        var candidates = await db.SandboxInstances
            .Where(s => s.OwnerId == ownerId)
            .ToListAsync(ct);
        return candidates.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
    }

    public async Task<IReadOnlyList<SandboxInstance>> GetIdleCandidatesAsync(DateTimeOffset sessionCutoff, DateTimeOffset ephemeralCutoff, CancellationToken ct)
    {
        var running = await db.SandboxInstances
            .Where(s => s.Status == SandboxInstanceStatus.Running)
            .ToListAsync(ct);

        return running.Where(s =>
        {
            var idleSince = s.LastUsedAt ?? s.CreatedAt;
            return s.Kind == SandboxKind.Session
                ? idleSince < sessionCutoff
                : idleSince < ephemeralCutoff;
        }).ToList();
    }

    public async Task<IReadOnlyList<SandboxInstance>> GetAllActiveAsync(CancellationToken ct)
    {
        return await db.SandboxInstances
            .Where(s => s.Status != SandboxInstanceStatus.Destroyed)
            .ToListAsync(ct);
    }
}
