using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Agents;

public interface IAgentRunsRepository
{
    Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct);

    /// <summary>Most recent runs for an agent, newest first.</summary>
    Task<List<AgentRun>> GetForAgentAsync(Guid agentId, int limit, CancellationToken ct);
}

public class AgentRunsRepository(AppDbContext db) : IAgentRunsRepository
{
    public async Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct)
    {
        return await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
    }

    public async Task<List<AgentRun>> GetForAgentAsync(Guid agentId, int limit, CancellationToken ct)
    {
        // Ordered client-side, same rationale as AgentsRepository: SQLite can't translate ORDER BY over
        // DateTimeOffset, and per-agent run counts for a local single-user app are small.
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.AgentId == agentId).ToListAsync(ct);
        return runs.OrderByDescending(r => r.StartedAt).Take(limit).ToList();
    }
}
