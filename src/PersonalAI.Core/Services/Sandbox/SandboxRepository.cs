using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Sandbox.Protocol;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Sandbox;

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

    public async Task<SandboxInstance> AddAsync(SandboxInstance instance, CancellationToken ct)
    {
        db.SandboxInstances.Add(instance);
        await db.SaveChangesAsync(ct);
        return instance;
    }

    public async Task UpdateAsync(SandboxInstance instance, CancellationToken ct)
    {
        db.SandboxInstances.Update(instance);
        await db.SaveChangesAsync(ct);
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

    public async Task<IReadOnlyList<SandboxInstance>> GetAllActiveAsync(CancellationToken ct) =>
        await db.SandboxInstances
            .Where(s => s.Status != SandboxInstanceStatus.Destroyed)
            .ToListAsync(ct);
}
