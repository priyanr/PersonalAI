using System.Text.Json;
using PersonalAI.Api.Streaming;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Services.Scheduling;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController(IAgentsRepository repo, AgentTypeRegistry agentTypes, IAgentRunsRepository runsRepo, IAgentExecutionService execution, ILogger<AgentsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentDto>>> GetAll(CancellationToken ct)
    {
        var agents = await repo.GetAllAsync(ct);
        return Ok(agents.Select(ToDto).ToList());
    }

    [HttpGet("types")]
    public ActionResult<IReadOnlyList<AgentTypeDto>> GetTypes() =>
        Ok(agentTypes.All.OrderBy(d => d.Type).Select(d => new AgentTypeDto(d.Type, d.DisplayName, d.Description)).ToList());

    [HttpPost]
    public async Task<ActionResult<AgentDto>> Create(CreateAgentRequest request, CancellationToken ct)
    {
        var error = Validate(request.Name, request.Prompt, request.ModelName,
            request.ScheduleKind, request.ScheduleOnceAt, request.IntervalMinutes, request.CronExpression, request.MaxLlmCalls);
        if (error is not null) return BadRequest(new { message = error });
        var (typeConfigJson, typeError) = NormalizeTypeConfig(request.Type, request.TypeConfig);
        if (typeError is not null) return BadRequest(new { message = typeError });
        if (!ModelSettings.TryNormalizeReasoningEffort(request.ReasoningEffort, out var reasoningEffort))
            return BadRequest(new { message = ModelSettings.InvalidReasoningEffortMessage(request.ReasoningEffort) });

        var agent = await repo.CreateAsync(
            request.Name.Trim(), request.Description, request.Prompt, request.ProviderKey ?? "ollama",
            request.ModelName, request.NumCtx, request.EnabledSkillIds ?? [], request.EnabledTools ?? [], request.MaxLlmCalls,
            request.ScheduleKind, request.ScheduleOnceAt, request.IntervalMinutes, request.CronExpression, request.Enabled, ct, reasoningEffort,
            request.Type, typeConfigJson!);
        return CreatedAtAction(nameof(Get), new { id = agent.Id }, ToDto(agent));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AgentDto>> Get(Guid id, CancellationToken ct)
    {
        var agent = await repo.GetAsync(id, ct);
        return agent is null ? NotFound() : Ok(ToDto(agent));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<AgentDto>> Update(Guid id, UpdateAgentRequest request, CancellationToken ct)
    {
        var error = Validate(request.Name, request.Prompt, request.ModelName,
            request.ScheduleKind, request.ScheduleOnceAt, request.IntervalMinutes, request.CronExpression, request.MaxLlmCalls);
        if (error is not null) return BadRequest(new { message = error });
        if (!ModelSettings.TryNormalizeReasoningEffort(request.ReasoningEffort, out var reasoningEffort))
            return BadRequest(new { message = ModelSettings.InvalidReasoningEffortMessage(request.ReasoningEffort) });

        var existing = await repo.GetAsync(id, ct);
        if (existing is null) return NotFound();

        // Omitted type: keep the current one. Omitted config for the same type: keep the current settings. A different
        // type with no config gets that type's defaults.
        var type = request.Type ?? existing.Type;
        var (typeConfigJson, typeError) = request.TypeConfig is null && type == existing.Type
            ? (existing.TypeConfigJson, null)
            : NormalizeTypeConfig(type, request.TypeConfig);
        if (typeError is not null) return BadRequest(new { message = typeError });

        var agent = await repo.UpdateAsync(
            id, request.Name.Trim(), request.Description, request.Prompt, request.ProviderKey ?? "ollama",
            request.ModelName, request.NumCtx, request.EnabledSkillIds ?? [], request.EnabledTools ?? [], request.MaxLlmCalls,
            request.ScheduleKind, request.ScheduleOnceAt, request.IntervalMinutes, request.CronExpression, request.Enabled, ct, reasoningEffort,
            type, typeConfigJson!);
        return agent is null ? NotFound() : Ok(ToDto(agent));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await repo.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/run")]
    public async Task Run(Guid id, RunAgentRequest request, CancellationToken ct)
    {
        SseEventWriter.PrepareResponse(Response);
        try
        {
            await foreach (var evt in execution.RunAsync(id, AgentRunTrigger.Manual, ct))
                await SseEventWriter.WriteAsync(Response, evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error while running agent {AgentId}", id);
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
    }

    /// <summary>Chat on a finished run: streams the agent's reply to a follow-up message, which is appended to the run.</summary>
    [HttpPost("{id:guid}/runs/{runId:guid}/messages")]
    public async Task<IActionResult> SendRunMessage(Guid id, Guid runId, RunMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest(new { message = "A message is required." });
        var run = await runsRepo.GetAsync(runId, ct);
        if (run is null || run.AgentId != id) return NotFound();
        if (run.Status == AgentRunStatus.Running) return Conflict(new { message = "This run is still in progress." });

        SseEventWriter.PrepareResponse(Response);
        try
        {
            await foreach (var evt in execution.ContinueAsync(id, runId, request.Message, ct))
                await SseEventWriter.WriteAsync(Response, evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error while continuing run {RunId} of agent {AgentId}", runId, id);
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
        return new EmptyResult();
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<ActionResult<IReadOnlyList<AgentRunSummaryDto>>> GetRuns(Guid id, [FromQuery] int limit, CancellationToken ct)
    {
        var runs = await runsRepo.GetForAgentAsync(id, limit <= 0 ? 20 : Math.Min(limit, 200), ct);
        return Ok(runs.Select(ToSummaryDto).ToList());
    }

    [HttpGet("{id:guid}/runs/{runId:guid}")]
    public async Task<ActionResult<AgentRunDetailDto>> GetRun(Guid id, Guid runId, CancellationToken ct)
    {
        var run = await runsRepo.GetAsync(runId, ct);
        if (run is null || run.AgentId != id) return NotFound();

        var transcript = JsonSerializer.Deserialize<List<AgentRunTranscriptEntry>>(run.TranscriptJson) ?? [];
        return Ok(new AgentRunDetailDto(run.Id, run.AgentId, run.Trigger, run.Status, run.StartedAt, run.CompletedAt, run.Summary, run.LlmCallCount,
            run.TotalInputTokens, run.TotalOutputTokens, transcript));
    }

    private static string? Validate(
        string? name, string? prompt, string? modelName, AgentScheduleKind scheduleKind,
        DateTimeOffset? scheduleOnceAt, int? intervalMinutes, string? cronExpression, int? maxLlmCalls)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(prompt)) return "Prompt is required.";
        if (string.IsNullOrWhiteSpace(modelName)) return "ModelName is required.";
        if (maxLlmCalls is <= 0) return "MaxLlmCalls must be a positive number when set.";

        // A throwaway instance purely to reuse ScheduleCalculator's field validation for this schedule kind.
        var probe = new Agent
        {
            Name = name,
            Prompt = prompt,
            ModelName = modelName,
            ScheduleKind = scheduleKind,
            ScheduleOnceAt = scheduleOnceAt,
            IntervalMinutes = intervalMinutes,
            CronExpression = cronExpression
        };
        return ScheduleCalculator.Validate(probe);
    }

    private (string? Json, string? Error) NormalizeTypeConfig(AgentType type, JsonElement? typeConfig) =>
        Enum.IsDefined(type)
            ? agentTypes.Get(type).NormalizeConfig(typeConfig is { ValueKind: JsonValueKind.Object } c ? c.GetRawText() : null)
            : (null, $"Unknown agent type {(int)type}.");

    private static AgentDto ToDto(Agent a) => new(
        a.Id,
        a.Name,
        a.Description,
        a.Type,
        JsonSerializer.Deserialize<JsonElement>(a.TypeConfigJson),
        a.Prompt,
        a.ProviderKey,
        a.ModelName,
        a.NumCtx,
        JsonSerializer.Deserialize<List<Guid>>(a.EnabledSkillIdsJson) ?? [],
        JsonSerializer.Deserialize<List<string>>(a.EnabledToolsJson) ?? [],
        a.MaxLlmCalls,
        a.ReasoningEffort,
        a.ScheduleKind,
        a.ScheduleOnceAt,
        a.IntervalMinutes,
        a.CronExpression,
        a.Enabled,
        a.NextRunAt,
        a.LastRunAt,
        a.CreatedAt,
        a.UpdatedAt);

    private static AgentRunSummaryDto ToSummaryDto(AgentRun r) => new(
        r.Id, r.AgentId, r.Trigger, r.Status, r.StartedAt, r.CompletedAt, r.Summary, r.LlmCallCount, r.TotalInputTokens, r.TotalOutputTokens);
}
