using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Monitoring.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

/// <summary>An agent's collection jobs. The model proposes them (collector_save); a person approves, pauses, runs and
/// removes them here. Nothing a model proposes runs unattended until it is approved (when the agent requires it).</summary>
[ApiController]
[Route("api/agents/{agentId:guid}/jobs")]
public class CollectionJobsController(CollectionJobService jobs) : ControllerBase
{
    [HttpGet]
    public async Task<List<CollectionJobDto>> GetAll(Guid agentId, CancellationToken ct)
    {
        return await jobs.GetJobsAsync(agentId, ct);
    }

    [HttpPost("{jobId:guid}/approve")]
    public async Task<CollectionJobDto> Approve(Guid agentId, Guid jobId, ApproveJobRequest request, CancellationToken ct)
    {
        return await jobs.ApproveAsync(agentId, jobId, request.Token, ct);
    }

    [HttpPost("{jobId:guid}/enable")]
    public async Task<CollectionJobDto> Enable(Guid agentId, Guid jobId, CancellationToken ct)
    {
        return await jobs.SetEnabledAsync(agentId, jobId, true, ct);
    }

    [HttpPost("{jobId:guid}/disable")]
    public async Task<CollectionJobDto> Disable(Guid agentId, Guid jobId, CancellationToken ct)
    {
        return await jobs.SetEnabledAsync(agentId, jobId, false, ct);
    }

    /// <summary>Runs one active job now, on every connection it applies to.</summary>
    [HttpPost("{jobId:guid}/run")]
    public async Task<RunNowResultDto> Run(Guid agentId, Guid jobId, CancellationToken ct)
    {
        return await jobs.RunNowAsync(agentId, jobId, ct);
    }

    [HttpDelete("{jobId:guid}")]
    public async Task<IActionResult> Delete(Guid agentId, Guid jobId, CancellationToken ct)
    {
        await jobs.DeleteAsync(agentId, jobId, ct);
        return NoContent();
    }
}
