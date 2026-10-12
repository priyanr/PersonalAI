using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Interfaces;

public interface IAgentsRepository
{
    Task<IReadOnlyList<Agent>> GetAllAsync(CancellationToken ct);

    Task<Agent?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Agents the scheduler should fire now: Enabled, with a non-null NextRunAt at or before <paramref name="asOfUtc"/>.</summary>
    Task<IReadOnlyList<Agent>> GetDueAsync(DateTimeOffset asOfUtc, CancellationToken ct);

    Task<Agent> CreateAsync(
        string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<Guid> enabledSkillIds, IReadOnlyList<string> enabledTools, int? maxLlmCalls, AgentScheduleKind scheduleKind,
        DateTimeOffset? scheduleOnceAt, int? intervalMinutes, string? cronExpression, bool enabled, CancellationToken ct,
        string? reasoningEffort = null);

    /// <summary>Returns null if no agent has that id.</summary>
    Task<Agent?> UpdateAsync(
        Guid id, string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<Guid> enabledSkillIds, IReadOnlyList<string> enabledTools, int? maxLlmCalls, AgentScheduleKind scheduleKind,
        DateTimeOffset? scheduleOnceAt, int? intervalMinutes, string? cronExpression, bool enabled, CancellationToken ct,
        string? reasoningEffort = null);

    /// <summary>Returns false if no agent has that id.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Persists the outcome of a run: LastRunAt and the freshly computed NextRunAt.</summary>
    Task SetRunOutcomeAsync(Guid id, DateTimeOffset lastRunAt, DateTimeOffset? nextRunAt, CancellationToken ct);
}
