using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Tools;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Core.Services.Sandbox;

/// <summary>Get-or-create/recreate logic for a SandboxInstance, shared between SandboxTool (the model's own
/// entry point, which always knows its owner's SandboxOwnerKind) and the sandbox file browser's REST
/// endpoints (which only ever browse a sandbox that already exists — they never create one from nothing).
/// Both entry points reuse a Running sandbox as-is and transparently recreate an Orphaned/idle-reaped one,
/// preserving its previous ports, since the host directory survives independently of the container.</summary>
public sealed class SandboxProvisioningService(
    ISandboxHostClient hostClient,
    ISandboxRepository repository)
{
    /// <summary>Full get-or-create: creates a brand-new SandboxInstance row for this owner if none exists
    /// yet. Used by SandboxTool, which always has a SandboxOwner (and therefore knows the SandboxKind to
    /// create with) even on an owner's very first sandbox call.</summary>
    public async Task<(Guid SandboxId, IReadOnlyList<int> Ports, bool WasRecreated, string? Error)> EnsureAsync(
        SandboxOwner owner, string image, CancellationToken ct)
    {
        var kind = MapKind(owner.Kind);

        var instance = await repository.GetActiveByOwnerAsync(owner.Id, ct);
        var isFirstLookupForOwner = instance is null;
        string? priorPortsJson = null;

        if (instance is null)
        {
            // Cold path only — never queried when a row is already Running, so this doesn't add a second
            // query to the common per-call case. GetActiveByOwnerAsync filters out Destroyed rows (the
            // idle reaper retires a chat-session sandbox this way, files kept), so this status-unfiltered
            // lookup is what carries PortsJson forward across that boundary too, the same way it already
            // does across the Orphaned one below.
            var previous = await repository.GetMostRecentByOwnerAsync(owner.Id, ct);
            priorPortsJson = previous?.PortsJson;

            instance = await repository.AddAsync(new SandboxInstance
            {
                Kind = kind,
                OwnerId = owner.Id,
                Image = image,
                Status = SandboxInstanceStatus.Creating,
                PortsJson = priorPortsJson ?? "[]"
            }, ct);
        }

        var wasRecreated = false;
        if (instance.Status != SandboxInstanceStatus.Running)
        {
            wasRecreated = !isFirstLookupForOwner || priorPortsJson is not null;
            var error = await RecreateContainerAsync(instance, kind, owner.Id, ct);
            if (error is not null)
                return (default, [], false, $"Failed to create the sandbox: {error}");
        }

        instance.LastUsedAt = DateTimeOffset.UtcNow;
        await repository.UpdateAsync(instance, ct);
        return (instance.Id, ParsePorts(instance.PortsJson), wasRecreated, null);
    }

    /// <summary>Called mid-tool-call when the broker itself reports (ExecCompleted.ContainerMissing /
    /// FileOpResponse.ContainerMissing) that a sandbox's container is gone even though the DB row still
    /// says Running -- the gap EnsureAsync's own `Status != Running` check can't catch, since nothing
    /// re-verifies a Running row between broker (re)connects (see SandboxReconciler, which only runs on
    /// connect). Flips the row to Orphaned first -- RecreateContainerAsync only ever sets Status back to
    /// Running on success and just records LastError on failure, so without this the row would stay
    /// falsely Running forever if the recreate attempt itself also fails -- then reuses
    /// RecreateContainerAsync exactly as EnsureAsync/TryEnsureExistingAsync do, preserving the row's
    /// existing ports.</summary>
    public async Task<(bool Recreated, IReadOnlyList<int> Ports, string? Error)> RecreateAfterContainerMissingAsync(
        SandboxOwner owner, CancellationToken ct)
    {
        var instance = await repository.GetActiveByOwnerAsync(owner.Id, ct);
        if (instance is null)
            return (false, [], "The sandbox no longer has an active row for this owner.");

        instance.Status = SandboxInstanceStatus.Orphaned;
        await repository.UpdateAsync(instance, ct);

        var error = await RecreateContainerAsync(instance, MapKind(owner.Kind), owner.Id, ct);
        if (error is not null)
            return (false, [], error);

        instance.LastUsedAt = DateTimeOffset.UtcNow;
        await repository.UpdateAsync(instance, ct);
        return (true, ParsePorts(instance.PortsJson), null);
    }

    /// <summary>Browsing-only variant: never creates a sandbox that never existed for this owner — a file
    /// browser opened for an owner with no sandbox history has nothing to show, and shouldn't spin one up
    /// just because someone looked. Looks at the most recent row regardless of status (including
    /// Destroyed) since a chat session's Destroyed row still has its files on disk (the idle reaper keeps
    /// them) and is recreated the same way an Orphaned one is. The one case treated as permanently gone:
    /// an Ephemeral (skill/agent-run) sandbox that reached Destroyed — both SkillExecutionService and
    /// AgentExecutionService call DestroySandboxAsync(..., deleteFiles: true, ...) unconditionally when a
    /// run ends, and the reaper's ephemeral safety net does the same, so there is nothing left to
    /// browse.</summary>
    public async Task<(bool Found, Guid SandboxId, IReadOnlyList<int> Ports, SandboxInstanceStatus? Status, string? Error)> TryEnsureExistingAsync(
        Guid ownerId, CancellationToken ct)
    {
        var instance = await repository.GetMostRecentByOwnerAsync(ownerId, ct);
        if (instance is null || IsPermanentlyGone(instance))
            return (false, default, [], null, null);

        if (instance.Status != SandboxInstanceStatus.Running)
        {
            var error = await RecreateContainerAsync(instance, instance.Kind, ownerId, ct);
            if (error is not null)
                return (true, default, [], instance.Status, $"Failed to reconnect to the sandbox: {error}");
        }

        instance.LastUsedAt = DateTimeOffset.UtcNow;
        await repository.UpdateAsync(instance, ct);
        return (true, instance.Id, ParsePorts(instance.PortsJson), instance.Status, null);
    }

    /// <summary>Shared by EnsureAsync and TryEnsureExistingAsync — the only difference between them is
    /// what they do with a brand-new-vs-reused row and how they report "not found", not this recreation
    /// mechanics. On success, applies EnsureSandboxAsync's result to `instance` (Running/HostPath/
    /// PortsJson/LastError) and returns null; on failure, records and persists LastError and returns the
    /// exception message for the caller to wrap into its own return shape.</summary>
    private async Task<string?> RecreateContainerAsync(SandboxInstance instance, SandboxKind kind, Guid ownerId, CancellationToken ct)
    {
        try
        {
            // Pass back whatever ports this owner had before (empty on first creation) so a container
            // recreated after being orphaned — or after the idle reaper retired it — keeps the same ones —
            // a server the user already pointed a browser at shouldn't move to a different port mid-session
            // just because the container needed to be recreated.
            var preferredPorts = ParsePorts(instance.PortsJson);
            var result = await hostClient.EnsureSandboxAsync(instance.Id, kind, ownerId, instance.Image, preferredPorts, ct);
            instance.Status = SandboxInstanceStatus.Running;
            instance.HostPath = result.HostPath;
            instance.PortsJson = JsonSerializer.Serialize(result.Ports);
            instance.LastError = null;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            instance.LastError = ex.Message;
            await repository.UpdateAsync(instance, ct);
            return ex.Message;
        }
    }

    /// <summary>Pure read, no broker call and no side effects on the row — safe to call on every page
    /// load/poll (e.g. to gate a "Sandbox files" button) without accidentally recreating a container just
    /// because someone looked. Uses the same "Ephemeral + Destroyed = gone" rule as TryEnsureExistingAsync;
    /// everything else (Session kind regardless of status, or Ephemeral not yet Destroyed) is reported
    /// browsable even though its container may need recreating on the first real request.</summary>
    public async Task<(bool CanBrowse, SandboxInstanceStatus? Status, IReadOnlyList<int> Ports)> CheckBrowsableAsync(
        Guid ownerId, CancellationToken ct)
    {
        var instance = await repository.GetMostRecentByOwnerAsync(ownerId, ct);
        if (instance is null || IsPermanentlyGone(instance))
            return (false, instance?.Status, []);

        return (true, instance.Status, ParsePorts(instance.PortsJson));
    }

    /// <summary>An Ephemeral (skill/agent-run) sandbox that reached Destroyed is gone for good — both
    /// SkillExecutionService and AgentExecutionService call DestroySandboxAsync(..., deleteFiles: true,
    /// ...) unconditionally when a run ends, and the reaper's ephemeral safety net does the same, so there
    /// is nothing left on disk to browse or recreate. A Session (chat) sandbox's Destroyed row, by
    /// contrast, only means the idle reaper retired the container — its files are kept, so it's still
    /// browsable (recreating the container on demand).</summary>
    private static bool IsPermanentlyGone(SandboxInstance instance) =>
        instance.Kind == SandboxKind.Ephemeral && instance.Status == SandboxInstanceStatus.Destroyed;

    private static IReadOnlyList<int> ParsePorts(string portsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(portsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SandboxKind MapKind(SandboxOwnerKind kind) => kind switch
    {
        SandboxOwnerKind.ChatSession => SandboxKind.Session,
        _ => SandboxKind.Ephemeral
    };
}
