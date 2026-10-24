using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Monitoring;
using PersonalAI.Core.Services.Monitoring.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

/// <summary>Collected data and on-demand collection for Dashboard &amp; Alerts agents.</summary>
[ApiController]
[Route("api/agents/{agentId:guid}")]
public class AgentDataController(DatasetService datasets, CollectionJobService jobs) : ControllerBase
{
    [HttpGet("datasets")]
    public async Task<List<DatasetDto>> GetDatasets(Guid agentId, CancellationToken ct)
    {
        return await datasets.GetDatasets(agentId, ct);
    }

    /// <summary>Points oldest-first. <c>latest=true</c> returns each entity's newest point only.</summary>
    [HttpGet("datasets/{name}/points")]
    public async Task<List<DataPointDto>> GetPoints(
        Guid agentId, string name, [FromQuery] string? entity, [FromQuery] int? sinceMinutes,
        [FromQuery] bool latest, [FromQuery] int? limit, CancellationToken ct)
    {
        return await datasets.GetPoints(agentId, name, entity, sinceMinutes, latest, limit, ct);
    }

    /// <summary>Runs every active collection job now, on every connection it applies to.</summary>
    [HttpPost("collect-now")]
    public async Task<RunNowResultDto> CollectNow(Guid agentId, CancellationToken ct)
    {
        return await jobs.RunNowAsync(agentId, null, ct);
    }
}
