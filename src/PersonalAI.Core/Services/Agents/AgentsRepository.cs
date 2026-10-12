using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Scheduling;
using Microsoft.EntityFrameworkCore;
// Alias needed: PersonalAI.Core.Services.Agent (the ChatAgentService namespace) is a sibling of this
// file's namespace, so the unqualified type name "Agent" resolves to that namespace instead of the
// Models.Entities.Agent entity type — alias to a non-colliding name instead.
using AgentEntity = PersonalAI.Core.Models.Entities.Agent;

namespace PersonalAI.Core.Services.Agents;

public class AgentsRepository(AppDbContext db) : IAgentsRepository
{
    public async Task<IReadOnlyList<AgentEntity>> GetAllAsync(CancellationToken ct)
    {
        // Ordered client-side, same rationale as SkillsRepository: SQLite can't translate ORDER BY over
        // DateTimeOffset, and agent counts for a local single-user app are tiny.
        var agents = await db.Agents.ToListAsync(ct);
        return agents.OrderByDescending(a => a.UpdatedAt).ToList();
    }

    public async Task<AgentEntity?> GetAsync(Guid id, CancellationToken ct) =>
        await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<AgentEntity>> GetDueAsync(DateTimeOffset asOfUtc, CancellationToken ct)
    {
        var agents = await db.Agents.Where(a => a.Enabled && a.NextRunAt != null).ToListAsync(ct);
        return agents.Where(a => a.NextRunAt <= asOfUtc).ToList();
    }

    public async Task<AgentEntity> CreateAsync(
        string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<Guid> enabledSkillIds, IReadOnlyList<string> enabledTools, int? maxLlmCalls, AgentScheduleKind scheduleKind,
        DateTimeOffset? scheduleOnceAt, int? intervalMinutes, string? cronExpression, bool enabled, CancellationToken ct,
        string? reasoningEffort = null)
    {
        var agent = new AgentEntity
        {
            Name = name,
            Description = description,
            Prompt = prompt,
            ProviderKey = providerKey,
            ModelName = modelName,
            NumCtx = numCtx,
            EnabledSkillIdsJson = JsonSerializer.Serialize(enabledSkillIds),
            EnabledToolsJson = JsonSerializer.Serialize(enabledTools),
            MaxLlmCalls = maxLlmCalls,
            ReasoningEffort = reasoningEffort,
            ScheduleKind = scheduleKind,
            ScheduleOnceAt = scheduleOnceAt,
            IntervalMinutes = intervalMinutes,
            CronExpression = cronExpression,
            Enabled = enabled
        };
        agent.NextRunAt = ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow);

        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        return agent;
    }

    public async Task<AgentEntity?> UpdateAsync(
        Guid id, string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<Guid> enabledSkillIds, IReadOnlyList<string> enabledTools, int? maxLlmCalls, AgentScheduleKind scheduleKind,
        DateTimeOffset? scheduleOnceAt, int? intervalMinutes, string? cronExpression, bool enabled, CancellationToken ct,
        string? reasoningEffort = null)
    {
        var agent = await db.Agents.FindAsync([id], ct);
        if (agent is null) return null;

        agent.Name = name;
        agent.Description = description;
        agent.Prompt = prompt;
        agent.ProviderKey = providerKey;
        agent.ModelName = modelName;
        agent.NumCtx = numCtx;
        agent.EnabledSkillIdsJson = JsonSerializer.Serialize(enabledSkillIds);
        agent.EnabledToolsJson = JsonSerializer.Serialize(enabledTools);
        agent.MaxLlmCalls = maxLlmCalls;
        agent.ReasoningEffort = reasoningEffort;
        agent.ScheduleKind = scheduleKind;
        agent.ScheduleOnceAt = scheduleOnceAt;
        agent.IntervalMinutes = intervalMinutes;
        agent.CronExpression = cronExpression;
        agent.Enabled = enabled;
        agent.UpdatedAt = DateTimeOffset.UtcNow;
        // Re-arm the schedule from now — a changed cadence (or Once instant) should take effect
        // immediately rather than waiting for a stale NextRunAt to catch up.
        agent.NextRunAt = ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow);

        await db.SaveChangesAsync(ct);
        return agent;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var agent = await db.Agents.FindAsync([id], ct);
        if (agent is null) return false;

        db.Agents.Remove(agent);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SetRunOutcomeAsync(Guid id, DateTimeOffset lastRunAt, DateTimeOffset? nextRunAt, CancellationToken ct)
    {
        var agent = await db.Agents.FindAsync([id], ct);
        if (agent is null) return;

        agent.LastRunAt = lastRunAt;
        agent.NextRunAt = nextRunAt;
        await db.SaveChangesAsync(ct);
    }
}
