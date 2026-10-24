using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Services.Monitoring.Alerts;

public record AlertEvaluationResult(int Fired, int Resolved);

/// <summary>Checks an agent's enabled alert rules against the newest data point of every entity, after each collection
/// pass, without a model. Per rule and entity:
/// - a violating sample opens a Pending alert, which fires once it has held for ForMinutes (in data-point time);
/// - a clean sample resolves a firing alert, or drops a pending one;
/// - stale samples are ignored, so a collector outage neither opens nor resolves anything;
/// - an alert whose entity no longer matches, stops reporting the field or disappears is closed. "Disappeared" only
///   counts while the rest of the dataset is fresh: that is how a removed container differs from a collector that is down.</summary>
public sealed class AlertEvaluator(AppDbContext db, IAlertsRepository alerts, IDatasetRepository datasets, ILogger<AlertEvaluator> logger)
{
    private const int FetchLimit = 2000;

    /// <summary>How long an entity may go without a new sample, while its dataset keeps updating, before its open
    /// alert is closed as "no longer reporting".</summary>
    public static readonly TimeSpan GoneAfter = TimeSpan.FromHours(1);

    /// <param name="freshness">Per-dataset freshness windows (see CollectionJobService.FreshnessWindows). A dataset
    /// written every few hours would otherwise always look stale against <paramref name="staleAfter"/> and never alert.</param>
    public async Task<AlertEvaluationResult> EvaluateAsync(
        Guid agentId, DateTime now, TimeSpan staleAfter, CancellationToken ct, IReadOnlyDictionary<string, TimeSpan>? freshness = null)
    {
        int fired = 0, resolved = 0;
        foreach (var rule in (await alerts.GetRulesAsync(agentId, ct)).Where(r => r.Enabled))
        {
            try
            {
                var stale = freshness is not null && freshness.TryGetValue(rule.Dataset, out var w) && w > staleAfter ? w : staleAfter;
                var (f, r) = await EvaluateRuleAsync(rule, now, stale, ct);
                fired += f;
                resolved += r;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken rule must not stop the rest from being evaluated.
                logger.LogError(ex, "Evaluating alert rule {Rule} for agent {AgentId} failed.", rule.Name, agentId);
            }
        }
        return new AlertEvaluationResult(fired, resolved);
    }

    private async Task<(int Fired, int Resolved)> EvaluateRuleAsync(AlertRule rule, DateTime now, TimeSpan staleAfter, CancellationToken ct)
    {
        var raw = await datasets.QueryAsync(rule.AgentId, rule.Dataset, null, null, null, true, FetchLimit, ct);
        if (raw is null) return (0, 0); // dataset gone: nothing to evaluate

        var filter = string.IsNullOrEmpty(rule.FilterJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(rule.FilterJson);
        var allRows = MonitoringRows.Parse(raw);
        var rows = MonitoringRows.Filter(allRows, rule.Target, filter);
        var open = (await alerts.GetOpenEventsAsync(rule.Id, ct)).ToDictionary(e => e.EntityKey);

        int fired = 0, resolved = 0;
        var drop = new List<AlertEvent>();
        var evaluated = new HashSet<string>();

        void Close(AlertEvent e, DateTime at, string reason)
        {
            if (e.State == AlertState.Pending)
            {
                drop.Add(e); // never fired, so there is nothing to resolve
                return;
            }
            e.Resolve(at, reason);
            resolved++;
        }

        foreach (var row in rows)
        {
            if (now - row.Ts > staleAfter) continue;
            var (value, isBool) = MonitoringRows.Read(row.Values, rule.Field);
            open.TryGetValue(row.EntityKey, out var current);

            if (value is null)
            {
                // A fresh sample that no longer carries the field (a server that just went down has no CPU reading):
                // the condition can't hold, so close what was open. Other rules (reachability) take over.
                if (current is not null)
                {
                    evaluated.Add(row.EntityKey);
                    Close(current, row.Ts, $"{rule.Field} is no longer reported");
                }
                continue;
            }
            evaluated.Add(row.EntityKey);

            if (AlertOperators.Compare(value.Value, rule.Operator, rule.Threshold))
            {
                var message = AlertEvent.CapMessage(ViolationMessage(row.Label, rule, value.Value, isBool));
                if (current is null)
                {
                    var state = rule.ForMinutes <= 0 ? AlertState.Firing : AlertState.Pending;
                    db.AlertEvents.Add(new AlertEvent
                    {
                        RuleId = rule.Id, AgentId = rule.AgentId, EntityKey = row.EntityKey, EntityLabel = row.Label,
                        State = state, Value = value.Value, Message = message,
                        FirstSeen = row.Ts, LastSeen = row.Ts, FiredAt = state == AlertState.Firing ? row.Ts : null
                    });
                    if (state == AlertState.Firing) fired++;
                    continue;
                }

                current.Value = value.Value;
                current.Message = message;
                current.EntityLabel = row.Label;
                if (row.Ts > current.LastSeen) current.LastSeen = row.Ts;
                if (current.State == AlertState.Pending && row.Ts - current.FirstSeen >= TimeSpan.FromMinutes(rule.ForMinutes))
                {
                    current.State = AlertState.Firing;
                    current.FiredAt = row.Ts;
                    fired++;
                }
            }
            else if (current is not null)
            {
                current.LastSeen = row.Ts;
                current.Value = value.Value;
                if (current.State == AlertState.Pending)
                {
                    drop.Add(current);
                    continue;
                }
                current.State = AlertState.Resolved;
                current.ResolvedAt = row.Ts;
                current.Message = AlertEvent.CapMessage($"{row.Label}: {rule.Field} is back to normal ({Format(value.Value, isBool)})");
                resolved++;
            }
        }

        // Open alerts whose entity wasn't evaluated this pass.
        var matching = rows.Select(r => r.EntityKey).ToHashSet();
        var byKey = allRows.GroupBy(r => r.EntityKey).ToDictionary(g => g.Key, g => g.First());
        var datasetIsFresh = allRows.Any(r => now - r.Ts <= staleAfter);
        var goneAfter = GoneAfter > staleAfter ? GoneAfter : staleAfter;
        foreach (var (key, e) in open)
        {
            if (evaluated.Contains(key)) continue;

            string? reason = null;
            if (!matching.Contains(key))
            {
                if (byKey.ContainsKey(key)) reason = "it no longer matches the rule";
                else if (datasetIsFresh) reason = "it is no longer reporting";
            }
            else if (datasetIsFresh && now - byKey[key].Ts > goneAfter)
            {
                reason = "it is no longer reporting";
            }
            if (reason is not null) Close(e, now, reason);
        }

        db.AlertEvents.RemoveRange(drop);
        await db.SaveChangesAsync(ct);
        return (fired, resolved);
    }

    private static string ViolationMessage(string label, AlertRule rule, double value, bool isBool)
    {
        return isBool
            ? $"{label}: {rule.Field} is {Format(value, true)}"
            : $"{label}: {rule.Field} is {Format(value, false)} ({AlertOperators.Symbol(rule.Operator)} {Format(rule.Threshold, false)})";
    }

    private static string Format(double value, bool isBool)
    {
        return isBool ? (value >= 1 ? "true" : "false") : Math.Round(value, 1).ToString(CultureInfo.InvariantCulture);
    }
}
