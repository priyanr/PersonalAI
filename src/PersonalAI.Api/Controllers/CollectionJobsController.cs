using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Monitoring.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

public record CollectionJobDto(
    Guid Id, string Name, string? Description, string Tool, string Request, int IntervalMinutes,
    JsonElement Parser, JsonElement Dataset, List<string>? Targets, string Status, string Source,
    DateTimeOffset? LastRunAt, string? LastError, int? LastRowCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string ApprovalToken, bool OutsideAllowlist);

/// <summary>Approval names the exact content being approved: the job's <c>approvalToken</c> as the page loaded it.</summary>
public record ApproveJobRequest(string? Token);

public record RunNowItemDto(string Job, string Target, bool Success, int Rows, string? Error);
public record RunNowResultDto(int JobsRun, IReadOnlyList<RunNowItemDto> Results);

/// <summary>An agent's collection jobs. The model proposes them (collector_save); a person approves, pauses, runs and
/// removes them here. Nothing a model proposes runs unattended until it is approved (when the agent requires it).</summary>
[ApiController]
[Route("api/agents/{agentId:guid}/jobs")]
public class CollectionJobsController(
    IAgentsRepository agents, ICollectionJobsRepository jobs, CollectionJobService service, CollectionRunner runner) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CollectionJobDto>>> GetAll(Guid agentId, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        return Ok((await jobs.GetAllAsync(agentId, ct)).Select(ToDto).ToList());
    }

    [HttpPost("{jobId:guid}/approve")]
    public async Task<ActionResult<CollectionJobDto>> Approve(Guid agentId, Guid jobId, ApproveJobRequest request, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        if (string.IsNullOrEmpty(request.Token)) return BadRequest(new { message = "The approval token of the job you reviewed is required." });

        var result = await service.ApproveAsync(agentId, jobId, request.Token, ct);
        return result.Job is null ? Conflict(new { message = result.Error }) : Ok(ToDto(result.Job));
    }

    [HttpPost("{jobId:guid}/enable")]
    public Task<ActionResult<CollectionJobDto>> Enable(Guid agentId, Guid jobId, CancellationToken ct) => SetEnabled(agentId, jobId, true, ct);

    [HttpPost("{jobId:guid}/disable")]
    public Task<ActionResult<CollectionJobDto>> Disable(Guid agentId, Guid jobId, CancellationToken ct) => SetEnabled(agentId, jobId, false, ct);

    /// <summary>Runs one active job now, on every connection it applies to.</summary>
    [HttpPost("{jobId:guid}/run")]
    public async Task<ActionResult<RunNowResultDto>> Run(Guid agentId, Guid jobId, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        if (await jobs.GetAsync(agentId, jobId, ct) is not { } job) return NotFound();
        if (job.Status != JobStatus.Active) return Conflict(new { message = "Only an active job can be run. Approve or enable it first." });

        var result = await runner.RunAsync(agentId, JobSelection.All, jobId, ct);
        if (result is null) return BadRequest(new { message = "Agent not found or not a Dashboard & Alerts agent." });
        if (result.Skipped) return Conflict(new { message = "A collection pass is already running for this agent." });
        return Ok(new RunNowResultDto(result.JobsRun, result.Results.Select(r => new RunNowItemDto(r.Job, r.Target, r.Success, r.Rows, r.Error)).ToList()));
    }

    [HttpDelete("{jobId:guid}")]
    public async Task<IActionResult> Delete(Guid agentId, Guid jobId, CancellationToken ct) =>
        await service.DeleteAsync(agentId, jobId, ct) ? NoContent() : NotFound();

    private async Task<ActionResult<CollectionJobDto>> SetEnabled(Guid agentId, Guid jobId, bool enabled, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        var job = await service.SetEnabledAsync(agentId, jobId, enabled, ct);
        return job is null
            ? Conflict(new { message = "The job does not exist or is still waiting for approval (approve or delete it)." })
            : Ok(ToDto(job));
    }

    private async Task<bool> IsDashboardAgentAsync(Guid agentId, CancellationToken ct) =>
        await agents.GetAsync(agentId, ct) is { Type: AgentType.DashboardAlerts };

    private static CollectionJobDto ToDto(CollectionJob j) => new(
        j.Id, j.Name, j.Description, j.Tool, j.Request, j.IntervalMinutes,
        JsonSerializer.Deserialize<JsonElement>(j.ParserJson), JsonSerializer.Deserialize<JsonElement>(j.DatasetJson),
        string.IsNullOrEmpty(j.TargetsJson) ? null : JsonSerializer.Deserialize<List<string>>(j.TargetsJson),
        j.Status, j.Source, j.LastRunAt, j.LastError, j.LastRowCount, j.CreatedAt, j.UpdatedAt, CollectionJobService.ApprovalToken(j), j.OutsideAllowlist);
}
