using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Monitoring.Dashboards;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

public record DashboardDto(int Version, string? Reason, DateTimeOffset UpdatedAt, JsonElement Spec);
public record DashboardVersionDto(int Version, string? Reason, Guid? AgentRunId, DateTimeOffset CreatedAt);
public record DashboardDataDto(DateTimeOffset GeneratedAt, Dictionary<string, object> Widgets);

/// <summary>An agent's single dashboard: the saved spec and its versions, plus live widget data. The model
/// writes the spec (dashboard_save); this API only reads it, renders data, and restores old versions.</summary>
[ApiController]
[Route("api/agents/{agentId:guid}/dashboard")]
public class DashboardController(IAgentsRepository agents, DashboardService dashboards) : ControllerBase
{
    /// <summary>204 when the agent has no dashboard yet.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardDto>> Get(Guid agentId, [FromQuery] int? version, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();

        var stored = version is { } v ? await dashboards.GetVersionAsync(agentId, v, ct) : await dashboards.GetCurrentAsync(agentId, ct);
        if (stored is null) return version is null ? NoContent() : NotFound();
        return Ok(new DashboardDto(stored.Version, stored.Reason, stored.UpdatedAt, JsonSerializer.Deserialize<JsonElement>(stored.SpecJson)));
    }

    /// <summary>Live data for every widget of a version (default: current).</summary>
    [HttpGet("data")]
    public async Task<ActionResult<DashboardDataDto>> GetData(Guid agentId, [FromQuery] int? version, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();

        var stored = version is { } v ? await dashboards.GetVersionAsync(agentId, v, ct) : await dashboards.GetCurrentAsync(agentId, ct);
        if (stored is null) return NotFound();

        var (spec, error) = DashboardSpecJson.Parse(stored.SpecJson);
        if (spec is null) return Problem(error);
        return Ok(new DashboardDataDto(DateTimeOffset.UtcNow, await dashboards.BuildDataAsync(agentId, spec, ct)));
    }

    [HttpGet("versions")]
    public async Task<ActionResult<IReadOnlyList<DashboardVersionDto>>> GetVersions(Guid agentId, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        var versions = await dashboards.ListVersionsAsync(agentId, ct);
        return Ok(versions.Select(v => new DashboardVersionDto(v.Version, v.Reason, v.AgentRunId, v.CreatedAt)).ToList());
    }

    /// <summary>Makes an old version current again by saving it as a new version.</summary>
    [HttpPost("versions/{version:int}/restore")]
    public async Task<ActionResult<DashboardVersionDto>> Restore(Guid agentId, int version, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        var restored = await dashboards.RestoreAsync(agentId, version, ct);
        if (restored is null) return NotFound();
        return Ok(new DashboardVersionDto(restored.Value, $"Restored v{version}", null, DateTimeOffset.UtcNow));
    }

    private async Task<bool> IsDashboardAgentAsync(Guid agentId, CancellationToken ct) =>
        await agents.GetAsync(agentId, ct) is { Type: AgentType.DashboardAlerts };
}
