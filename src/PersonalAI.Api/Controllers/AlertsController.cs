using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Monitoring.Alerts;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

/// <summary>Alert rules (managed per agent by the user or the model) and the alerts they produce. Alerts are
/// evaluated automatically after each collection pass; this API reads them and lets a person acknowledge.</summary>
[ApiController]
[Route("api")]
public class AlertsController(AlertRuleService rules, AlertsService alerts) : ControllerBase
{
    [HttpGet("agents/{agentId:guid}/alert-rules")]
    public async Task<List<AlertRuleDto>> GetRules(Guid agentId, CancellationToken ct)
    {
        return await rules.GetRulesAsync(agentId, ct);
    }

    [HttpPost("agents/{agentId:guid}/alert-rules")]
    public async Task<AlertRuleDto> CreateRule(Guid agentId, AlertRuleInput input, CancellationToken ct)
    {
        return await rules.CreateUserRuleAsync(agentId, input, ct);
    }

    [HttpPatch("agents/{agentId:guid}/alert-rules/{ruleId:guid}")]
    public async Task<AlertRuleDto> UpdateRule(Guid agentId, Guid ruleId, AlertRuleInput input, CancellationToken ct)
    {
        return await rules.UpdateRuleAsync(agentId, ruleId, input, ct);
    }

    [HttpDelete("agents/{agentId:guid}/alert-rules/{ruleId:guid}")]
    public async Task<IActionResult> DeleteRule(Guid agentId, Guid ruleId, CancellationToken ct)
    {
        await rules.DeleteRuleAsync(agentId, ruleId, ct);
        return NoContent();
    }

    /// <summary><c>state</c>: <c>active</c> (default, firing), <c>resolved</c> (last 7 days) or <c>all</c>.</summary>
    [HttpGet("alerts")]
    public async Task<List<AlertDto>> GetAlerts([FromQuery] string? state, [FromQuery] Guid? agentId, CancellationToken ct)
    {
        return await alerts.GetAlerts(state, agentId, ct);
    }

    [HttpGet("alerts/summary")]
    public async Task<AlertSummaryDto> GetSummary(CancellationToken ct)
    {
        return await alerts.GetSummary(ct);
    }

    [HttpPost("alerts/{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        await alerts.Acknowledge(id, ct);
        return NoContent();
    }

    /// <summary>Takes an acknowledgement back, so the alert needs attention again.</summary>
    [HttpPost("alerts/{id:guid}/unacknowledge")]
    public async Task<IActionResult> Unacknowledge(Guid id, CancellationToken ct)
    {
        await alerts.Unacknowledge(id, ct);
        return NoContent();
    }

    /// <summary>Acknowledges every firing alert that has not been acknowledged yet, optionally of one agent.</summary>
    [HttpPost("alerts/acknowledge-all")]
    public async Task<AcknowledgeAllResultDto> AcknowledgeAll([FromQuery] Guid? agentId, CancellationToken ct)
    {
        return await alerts.AcknowledgeAll(agentId, ct);
    }
}
