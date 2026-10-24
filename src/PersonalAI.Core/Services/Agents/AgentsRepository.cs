using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;
// Alias needed: PersonalAI.Core.Services.Agent (the ChatAgentService namespace) is a sibling of this
// file's namespace, so the unqualified type name "Agent" resolves to that namespace instead of the
// Models.Entities.Agent entity type — alias to a non-colliding name instead.
using AgentEntity = PersonalAI.Core.Models.Entities.Agent;

namespace PersonalAI.Core.Services.Agents;

public interface IAgentsRepository
{
    Task<List<AgentEntity>> GetAllAsync(CancellationToken ct);

    Task<AgentEntity?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Agents the scheduler should fire now: Enabled, with a non-null NextRunAt at or before <paramref name="asOfUtc"/>.</summary>
    Task<List<AgentEntity>> GetDueAsync(DateTimeOffset asOfUtc, CancellationToken ct);

    /// <summary>True when the agent exists and is a Dashboard &amp; Alerts agent, the only kind that has jobs, data, a dashboard and alerts.</summary>
    Task<bool> IsDashboardAlertsAgentAsync(Guid id, CancellationToken ct);
}

public class AgentsRepository(AppDbContext db) : IAgentsRepository
{
    public async Task<List<AgentEntity>> GetAllAsync(CancellationToken ct)
    {
        // Ordered client-side, same rationale as SkillsRepository: SQLite can't translate ORDER BY over
        // DateTimeOffset, and agent counts for a local single-user app are tiny.
        var agents = await db.Agents.AsNoTracking().ToListAsync(ct);
        return agents.OrderByDescending(a => a.UpdatedAt).ToList();
    }

    public async Task<AgentEntity?> GetAsync(Guid id, CancellationToken ct)
    {
        return await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<List<AgentEntity>> GetDueAsync(DateTimeOffset asOfUtc, CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking().Where(a => a.Enabled && a.NextRunAt != null).ToListAsync(ct);
        return agents.Where(a => a.NextRunAt <= asOfUtc).ToList();
    }

    public async Task<bool> IsDashboardAlertsAgentAsync(Guid id, CancellationToken ct)
    {
        return await db.Agents.AnyAsync(a => a.Id == id && a.Type == AgentType.DashboardAlerts, ct);
    }
}
