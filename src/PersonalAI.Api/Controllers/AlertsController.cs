using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Monitoring.Alerts;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

public record AlertRuleDto(
    Guid Id, Guid AgentId, string Name, string? Description, string Dataset, string Field, string Operator,
    double Threshold, int ForMinutes, string Severity, string? Target, Dictionary<string, string>? Filter,
    bool Enabled, string Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record AlertDto(
    Guid Id, Guid AgentId, string AgentName, Guid RuleId, string RuleName, string Severity, string State,
    string EntityLabel, string Message, double Value, DateTime? FiredAt, DateTime LastSeen, DateTime? ResolvedAt,
    bool Acknowledged, DateTime? AcknowledgedAt);

/// <param name="Unacknowledged">Firing alerts that still need attention (what the nav badge counts).</param>
/// <param name="Acknowledged">Firing alerts someone has acknowledged; they stay listed until the condition clears.</param>
public record AlertSummaryDto(int Firing, int Critical, int Warning, int Unacknowledged, int UnacknowledgedCritical, int Acknowledged);

public record AcknowledgeAllResultDto(int Acknowledged);

/// <summary>Alert rules (managed per agent by the user or the model) and the alerts they produce. Alerts are
/// evaluated automatically after each collection pass; this API reads them and lets a person acknowledge.</summary>
[ApiController]
[Route("api")]
public class AlertsController(IAgentsRepository agents, IAlertsRepository alerts, AlertRuleService rules) : ControllerBase
{
    // ---- rules --------------------------------------------------------------------

    [HttpGet("agents/{agentId:guid}/alert-rules")]
    public async Task<ActionResult<IReadOnlyList<AlertRuleDto>>> GetRules(Guid agentId, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        return Ok((await alerts.GetRulesAsync(agentId, ct)).Select(ToDto).ToList());
    }

    [HttpPost("agents/{agentId:guid}/alert-rules")]
    public async Task<ActionResult<AlertRuleDto>> CreateRule(Guid agentId, AlertRuleInput input, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        var result = await rules.CreateAsync(agentId, input, AlertSources.User, ct);
        return result.IsValid ? Ok(ToDto(result.Rule!)) : BadRequest(new { message = string.Join(" ", result.Errors) });
    }

    [HttpPatch("agents/{agentId:guid}/alert-rules/{ruleId:guid}")]
    public async Task<ActionResult<AlertRuleDto>> UpdateRule(Guid agentId, Guid ruleId, AlertRuleInput input, CancellationToken ct)
    {
        if (!await IsDashboardAgentAsync(agentId, ct)) return NotFound();
        var result = await rules.UpdateAsync(agentId, ruleId, input, ct);
        if (result is null) return NotFound();
        return result.IsValid ? Ok(ToDto(result.Rule!)) : BadRequest(new { message = string.Join(" ", result.Errors) });
    }

    [HttpDelete("agents/{agentId:guid}/alert-rules/{ruleId:guid}")]
    public async Task<IActionResult> DeleteRule(Guid agentId, Guid ruleId, CancellationToken ct) =>
        await alerts.DeleteRuleAsync(agentId, ruleId, ct) ? NoContent() : NotFound();

    // ---- alerts -------------------------------------------------------------------

    /// <summary><c>state</c>: <c>active</c> (default, firing), <c>resolved</c> (last 7 days) or <c>all</c>.
    /// Firing first, then newest.</summary>
    [HttpGet("alerts")]
    public async Task<ActionResult<IReadOnlyList<AlertDto>>> GetAlerts(
        [FromQuery] string? state, [FromQuery] Guid? agentId, CancellationToken ct)
    {
        var active = state is null or "active" or "all";
        var resolved = state is "resolved" or "all";
        if (!active && !resolved) return BadRequest(new { message = "state must be active, resolved or all." });

        var events = await alerts.GetEventsAsync(agentId, active, resolved ? DateTime.UtcNow.AddDays(-7) : null, ct);
        if (events.Count == 0) return Ok(Array.Empty<AlertDto>());

        var agentNames = (await agents.GetAllAsync(ct)).ToDictionary(a => a.Id, a => a.Name);
        var ruleNames = new Dictionary<Guid, (string Name, string Severity)>();
        foreach (var id in events.Select(e => e.AgentId).Distinct())
            foreach (var r in await alerts.GetRulesAsync(id, ct)) ruleNames[r.Id] = (r.Name, r.Severity);

        return Ok(events.Select(e =>
        {
            var (ruleName, severity) = ruleNames.GetValueOrDefault(e.RuleId, ("(deleted rule)", AlertSeverity.Warning));
            return new AlertDto(e.Id, e.AgentId, agentNames.GetValueOrDefault(e.AgentId, "(deleted agent)"), e.RuleId, ruleName, severity,
                e.State.ToString().ToLowerInvariant(), e.EntityLabel, e.Message, e.Value, e.FiredAt, e.LastSeen, e.ResolvedAt, e.Acknowledged, e.AcknowledgedAt);
        })
        // Critical before warning within the firing group; the repository already ordered by recency.
        .OrderByDescending(a => a.State == "firing").ThenByDescending(a => a.Severity == AlertSeverity.Critical)
        .ToList());
    }

    [HttpGet("alerts/summary")]
    public async Task<ActionResult<AlertSummaryDto>> GetSummary(CancellationToken ct)
    {
        var s = await alerts.GetSummaryAsync(ct);
        return Ok(new AlertSummaryDto(s.Firing, s.Critical, s.Warning, s.Unacknowledged, s.UnacknowledgedCritical, s.Firing - s.Unacknowledged));
    }

    [HttpPost("alerts/{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct) =>
        await alerts.AcknowledgeAsync(id, ct) ? NoContent() : NotFound();

    /// <summary>Takes an acknowledgement back, so the alert needs attention again.</summary>
    [HttpPost("alerts/{id:guid}/unacknowledge")]
    public async Task<IActionResult> Unacknowledge(Guid id, CancellationToken ct) =>
        await alerts.UnacknowledgeAsync(id, ct) ? NoContent() : NotFound();

    /// <summary>Acknowledges every firing alert that has not been acknowledged yet, optionally of one agent.</summary>
    [HttpPost("alerts/acknowledge-all")]
    public async Task<ActionResult<AcknowledgeAllResultDto>> AcknowledgeAll([FromQuery] Guid? agentId, CancellationToken ct) =>
        Ok(new AcknowledgeAllResultDto(await alerts.AcknowledgeAllAsync(agentId, ct)));

    private async Task<bool> IsDashboardAgentAsync(Guid agentId, CancellationToken ct) =>
        await agents.GetAsync(agentId, ct) is { Type: AgentType.DashboardAlerts };

    private static AlertRuleDto ToDto(AlertRule r) => new(
        r.Id, r.AgentId, r.Name, r.Description, r.Dataset, r.Field, r.Operator, r.Threshold, r.ForMinutes, r.Severity, r.Target,
        string.IsNullOrEmpty(r.FilterJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.FilterJson),
        r.Enabled, r.Source, r.CreatedAt, r.UpdatedAt);
}
