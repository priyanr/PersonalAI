using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Agents;

public class AgentRunsRepository(AppDbContext db) : IAgentRunsRepository
{
    public async Task<AgentRun> CreateAsync(Guid agentId, AgentRunTrigger trigger, CancellationToken ct)
    {
        var run = new AgentRun { AgentId = agentId, Trigger = trigger };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct) =>
        await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);

    public async Task<IReadOnlyList<AgentRun>> GetForAgentAsync(Guid agentId, int limit, CancellationToken ct)
    {
        // Ordered client-side, same rationale as AgentsRepository: SQLite can't translate ORDER BY over
        // DateTimeOffset, and per-agent run counts for a local single-user app are small.
        var runs = await db.AgentRuns.Where(r => r.AgentId == agentId).ToListAsync(ct);
        return runs.OrderByDescending(r => r.StartedAt).Take(limit).ToList();
    }

    public async Task CompleteAsync(Guid runId, AgentRunStatus status, string? summary, string transcriptJson, CancellationToken ct)
    {
        var run = await db.AgentRuns.FindAsync([runId], ct);
        if (run is null) return;

        run.Status = status;
        run.Summary = summary;
        run.TranscriptJson = transcriptJson;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task AppendTranscriptAsync(Guid runId, string transcriptJson, CancellationToken ct)
    {
        var run = await db.AgentRuns.FindAsync([runId], ct);
        if (run is null) return;

        run.TranscriptJson = transcriptJson;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> IncrementLlmCallsAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.AgentRuns.FindAsync([runId], ct);
        if (run is null) return 0;

        run.LlmCallCount++;
        await db.SaveChangesAsync(ct);
        return run.LlmCallCount;
    }

    public async Task<(long InputTokens, long OutputTokens)> AddTokenUsageAsync(Guid runId, AiTokenUsage usage, CancellationToken ct)
    {
        var run = await db.AgentRuns.FindAsync([runId], ct);
        if (run is null) return (0, 0);

        run.TotalInputTokens += usage.InputTokens;
        run.TotalOutputTokens += usage.OutputTokens;
        await db.SaveChangesAsync(ct);
        return (run.TotalInputTokens, run.TotalOutputTokens);
    }
}
