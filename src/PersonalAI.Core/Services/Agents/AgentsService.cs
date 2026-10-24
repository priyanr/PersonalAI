using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Scheduling;
using AgentEntity = PersonalAI.Core.Models.Entities.Agent;

namespace PersonalAI.Core.Services.Agents;

/// <summary>Create, edit and delete Agents and read their runs (/api/agents). Running one is AgentExecutionService.</summary>
public class AgentsService(AppDbContext db, IAgentsRepository agents, IAgentRunsRepository runs, AgentTypeRegistry agentTypes)
{
    public async Task<List<AgentDto>> GetAll(CancellationToken ct)
    {
        return (await agents.GetAllAsync(ct)).Select(ToDto).ToList();
    }

    public List<AgentTypeDto> GetTypes()
    {
        return agentTypes.All.OrderBy(d => d.Type).Select(d => new AgentTypeDto(d.Type, d.DisplayName, d.Description)).ToList();
    }

    public async Task<AgentDto> Get(Guid id, CancellationToken ct)
    {
        return ToDto(await agents.GetAsync(id, ct) ?? throw NotFound(id));
    }

    public async Task<AgentDto> Create(SaveAgentRequest request, CancellationToken ct)
    {
        var type = request.Type ?? AgentType.General;
        var agent = new AgentEntity { Name = request.Name, Prompt = request.Prompt, ModelName = request.ModelName };
        Apply(agent, request, type, NormalizeTypeConfig(type, request.TypeConfig));

        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        return ToDto(agent);
    }

    public async Task<AgentDto> Update(Guid id, SaveAgentRequest request, CancellationToken ct)
    {
        var agent = await agents.GetAsync(id, ct) ?? throw NotFound(id);

        // Omitted type: keep the current one. Omitted config for the same type: keep the current settings. A different
        // type with no config gets that type's defaults.
        var type = request.Type ?? agent.Type;
        var typeConfigJson = request.TypeConfig is null && type == agent.Type
            ? agent.TypeConfigJson
            : NormalizeTypeConfig(type, request.TypeConfig);
        Apply(agent, request, type, typeConfigJson);
        agent.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return ToDto(agent);
    }

    public async Task Delete(Guid id, CancellationToken ct)
    {
        var agent = await agents.GetAsync(id, ct) ?? throw NotFound(id);
        db.Agents.Remove(agent);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AgentRunSummaryDto>> GetRuns(Guid id, int limit, CancellationToken ct)
    {
        var agentRuns = await runs.GetForAgentAsync(id, limit <= 0 ? 20 : Math.Min(limit, 200), ct);
        return agentRuns
            .Select(r => new AgentRunSummaryDto(
                r.Id, r.AgentId, r.Trigger, r.Status, r.StartedAt, r.CompletedAt, r.Summary, r.LlmCallCount, r.TotalInputTokens, r.TotalOutputTokens))
            .ToList();
    }

    public async Task<AgentRunDetailDto> GetRun(Guid id, Guid runId, CancellationToken ct)
    {
        var run = await GetRunOfAgent(id, runId, ct);
        var transcript = JsonSerializer.Deserialize<List<AgentRunTranscriptEntry>>(run.TranscriptJson) ?? [];
        return new AgentRunDetailDto(run.Id, run.AgentId, run.Trigger, run.Status, run.StartedAt, run.CompletedAt, run.Summary, run.LlmCallCount,
            run.TotalInputTokens, run.TotalOutputTokens, transcript);
    }

    /// <summary>Checked before a follow-up message starts streaming, so a bad request still gets a status code.</summary>
    public async Task EnsureRunAcceptsMessage(Guid id, Guid runId, string? message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidRequestException("A message is required.");
        var run = await GetRunOfAgent(id, runId, ct);
        if (run.Status == AgentRunStatus.Running) throw new ConflictException("This run is still in progress.");
    }

    private async Task<AgentRun> GetRunOfAgent(Guid id, Guid runId, CancellationToken ct)
    {
        var run = await runs.GetAsync(runId, ct);
        if (run is null || run.AgentId != id) throw new NotFoundException($"Run {runId} not found.");
        return run;
    }

    private static void Apply(AgentEntity agent, SaveAgentRequest request, AgentType type, string typeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidRequestException("Name is required.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidRequestException("Prompt is required.");
        if (string.IsNullOrWhiteSpace(request.ModelName)) throw new InvalidRequestException("ModelName is required.");
        if (request.MaxLlmCalls is <= 0) throw new InvalidRequestException("MaxLlmCalls must be a positive number when set.");
        if (!ModelSettings.TryNormalizeReasoningEffort(request.ReasoningEffort, out var reasoningEffort))
            throw new InvalidRequestException(ModelSettings.InvalidReasoningEffortMessage(request.ReasoningEffort));

        agent.ScheduleKind = request.ScheduleKind;
        agent.ScheduleOnceAt = request.ScheduleOnceAt;
        agent.IntervalMinutes = request.IntervalMinutes;
        agent.CronExpression = request.CronExpression;
        if (ScheduleCalculator.Validate(agent) is { } scheduleError) throw new InvalidRequestException(scheduleError);

        agent.Type = type;
        agent.TypeConfigJson = typeConfigJson;
        agent.Name = request.Name.Trim();
        agent.Description = request.Description;
        agent.Prompt = request.Prompt;
        agent.ProviderKey = request.ProviderKey ?? "ollama";
        agent.ModelName = request.ModelName;
        agent.NumCtx = request.NumCtx;
        agent.EnabledSkillIdsJson = JsonSerializer.Serialize(request.EnabledSkillIds ?? []);
        agent.EnabledToolsJson = JsonSerializer.Serialize(request.EnabledTools ?? []);
        agent.MaxLlmCalls = request.MaxLlmCalls;
        agent.ReasoningEffort = reasoningEffort;
        agent.Enabled = request.Enabled;
        // Armed from now on every save: a changed cadence (or Once instant) takes effect immediately rather than
        // waiting for a stale NextRunAt to catch up.
        agent.NextRunAt = ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow);
    }

    private string NormalizeTypeConfig(AgentType type, JsonElement? typeConfig)
    {
        if (!Enum.IsDefined(type)) throw new InvalidRequestException($"Unknown agent type {(int)type}.");
        var (json, error) = agentTypes.Get(type).NormalizeConfig(typeConfig is { ValueKind: JsonValueKind.Object } c ? c.GetRawText() : null);
        if (error is not null) throw new InvalidRequestException(error);
        return json!;
    }

    private static NotFoundException NotFound(Guid id)
    {
        return new NotFoundException($"Agent {id} not found.");
    }

    private static AgentDto ToDto(AgentEntity a)
    {
        return new AgentDto(
            a.Id, a.Name, a.Description, a.Type, JsonSerializer.Deserialize<JsonElement>(a.TypeConfigJson), a.Prompt,
            a.ProviderKey, a.ModelName, a.NumCtx,
            JsonSerializer.Deserialize<List<Guid>>(a.EnabledSkillIdsJson) ?? [],
            JsonSerializer.Deserialize<List<string>>(a.EnabledToolsJson) ?? [],
            a.MaxLlmCalls, a.ReasoningEffort, a.ScheduleKind, a.ScheduleOnceAt, a.IntervalMinutes, a.CronExpression,
            a.Enabled, a.NextRunAt, a.LastRunAt, a.CreatedAt, a.UpdatedAt);
    }
}
