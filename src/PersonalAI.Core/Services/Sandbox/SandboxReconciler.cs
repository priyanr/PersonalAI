using PersonalAI.Core.Models.Entities;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Core.Services.Sandbox;

/// <summary>Pure reconciliation logic for a broker (re)connect — no DB, no socket, so it is unit-testable
/// on its own (see Sandbox/ReconciliationTests). SandboxHostRegistry calls this after every HostHello and
/// applies the resulting transitions inside a fresh DI scope.</summary>
public static class SandboxReconciler
{
    public enum Transition
    {
        /// <summary>DB row + a reported container both alive — re-adopt by ContainerId.</summary>
        MarkRunning,

        /// <summary>DB row survives, no matching reported container — the files are still on disk
        /// (they live in a plain host directory keyed by owner), so the next tool call transparently
        /// creates a fresh container rather than losing anything.</summary>
        MarkOrphaned,

        /// <summary>A reported container with no matching active DB row (DB reset, stale deploy) — left
        /// alone for the reaper's grace period rather than destroyed immediately, since something may be
        /// mid-debug on that host.</summary>
        Untracked
    }

    public sealed record ReconciliationResult(SandboxInstance? Instance, Transition Transition, string? ContainerId);

    /// <summary><paramref name="dbRows"/> should already be filtered to non-Destroyed instances (see
    /// ISandboxRepository.GetAllActiveAsync); <paramref name="reported"/> is exactly what the broker's
    /// ListSandboxesResponse says is alive right now.</summary>
    public static IReadOnlyList<ReconciliationResult> Reconcile(
        IReadOnlyList<SandboxInstance> dbRows, IReadOnlyList<AdoptedSandbox> reported)
    {
        var reportedById = reported.ToDictionary(r => r.SandboxId);
        var results = new List<ReconciliationResult>(dbRows.Count + reported.Count);

        foreach (var row in dbRows)
        {
            results.Add(reportedById.TryGetValue(row.Id, out var adopted)
                ? new ReconciliationResult(row, Transition.MarkRunning, adopted.ContainerId)
                : new ReconciliationResult(row, Transition.MarkOrphaned, null));
        }

        var dbIds = dbRows.Select(r => r.Id).ToHashSet();
        foreach (var adopted in reported)
        {
            if (!dbIds.Contains(adopted.SandboxId))
                results.Add(new ReconciliationResult(null, Transition.Untracked, adopted.ContainerId));
        }

        return results;
    }
}
