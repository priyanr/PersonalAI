using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Monitoring.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

/// <summary>Collected data and on-demand collection for Dashboard &amp; Alerts agents.</summary>
[ApiController]
[Route("api/agents/{agentId:guid}")]
public class AgentDataController(IAgentsRepository agents, IDatasetRepository datasets, CollectionRunner runner) : ControllerBase
{
    [HttpGet("datasets")]
    public async Task<ActionResult<IReadOnlyList<DatasetDto>>> GetDatasets(Guid agentId, CancellationToken ct)
    {
        if (await agents.GetAsync(agentId, ct) is null) return NotFound();
        var list = await datasets.ListAsync(agentId, ct);
        return Ok(list.Select(d => new DatasetDto(d.Name, d.Kind.ToString(), d.SchemaHint, d.PointCount, AsUtc(d.LastTs))).ToList());
    }

    /// <summary>Points oldest-first. <c>latest=true</c> returns each entity's newest point only.</summary>
    [HttpGet("datasets/{name}/points")]
    public async Task<ActionResult<IReadOnlyList<DataPointDto>>> GetPoints(
        Guid agentId, string name, [FromQuery] string? entity, [FromQuery] int? sinceMinutes,
        [FromQuery] bool latest, [FromQuery] int? limit, CancellationToken ct)
    {
        if (await agents.GetAsync(agentId, ct) is null) return NotFound();

        var from = sinceMinutes is > 0 ? DateTime.UtcNow.AddMinutes(-sinceMinutes.Value) : (DateTime?)null;
        var rows = await datasets.QueryAsync(agentId, name, entity, null, from, latest, Math.Clamp(limit ?? 500, 1, 5000), ct);
        if (rows is null) return NotFound(new { message = $"No dataset named '{name}'." });
        return Ok(rows.Select(r => new DataPointDto(r.EntityKey, DateTime.SpecifyKind(r.Ts, DateTimeKind.Utc), r.ValuesJson)).ToList());
    }

    /// <summary>Runs every active collection job now, on every connection it applies to.</summary>
    [HttpPost("collect-now")]
    public async Task<ActionResult<RunNowResultDto>> CollectNow(Guid agentId, CancellationToken ct)
    {
        var result = await runner.RunAsync(agentId, JobSelection.All, null, ct);
        if (result is null) return BadRequest(new { message = "Agent not found or not a Dashboard & Alerts agent." });
        if (result.Skipped) return Conflict(new { message = "A collection pass is already running for this agent." });
        return Ok(new RunNowResultDto(result.JobsRun, result.Results.Select(r => new RunNowItemDto(r.Job, r.Target, r.Success, r.Rows, r.Error)).ToList()));
    }

    private static DateTime? AsUtc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
