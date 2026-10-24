using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Monitoring.Dashboards;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

/// <summary>An agent's single dashboard: the saved spec and its versions, plus live widget data. The model
/// writes the spec (dashboard_save); this API only reads it, renders data, and restores old versions.</summary>
[ApiController]
[Route("api/agents/{agentId:guid}/dashboard")]
public class DashboardController(DashboardService dashboards) : ControllerBase
{
    /// <summary>204 when the agent has no dashboard yet.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardDto>> Get(Guid agentId, [FromQuery] int? version, CancellationToken ct)
    {
        var dashboard = await dashboards.Get(agentId, version, ct);
        return dashboard is null ? NoContent() : dashboard;
    }

    [HttpGet("data")]
    public async Task<DashboardDataDto> GetData(Guid agentId, [FromQuery] int? version, CancellationToken ct)
    {
        return await dashboards.GetData(agentId, version, ct);
    }

    [HttpGet("versions")]
    public async Task<List<DashboardVersionDto>> GetVersions(Guid agentId, CancellationToken ct)
    {
        return await dashboards.GetVersions(agentId, ct);
    }

    [HttpPost("versions/{version:int}/restore")]
    public async Task<DashboardVersionDto> Restore(Guid agentId, int version, CancellationToken ct)
    {
        return await dashboards.Restore(agentId, version, ct);
    }
}
