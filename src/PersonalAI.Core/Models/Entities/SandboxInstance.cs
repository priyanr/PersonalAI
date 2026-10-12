using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Core.Models.Entities;

/// <summary>One Docker container the sandbox tool has run of — one per chat session (state survives
/// across turns) or one per skill/agent run (ephemeral, torn down with the run). The DB row is
/// authoritative for intent and ownership; the broker's live container list is authoritative for runtime
/// existence; the two are reconciled on every (re)connect (see PersonalAI.SandboxHost's reconciliation
/// pass in SandboxHostRegistry.ReconcileAsync).</summary>
public class SandboxInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();     // == personalai.sandbox.id label
    public SandboxKind Kind { get; set; }
    public Guid OwnerId { get; set; }                   // ChatSession.Id, or the skill/agent run id
    public required string Image { get; set; }
    public string? ContainerId { get; set; }

    /// <summary>Host directory bind-mounted at /work, e.g. /data/sandbox/&lt;OwnerId&gt;. Reported back
    /// by the broker on create; recorded for diagnostics and so the reaper can ask for it to be
    /// deleted.</summary>
    public string? HostPath { get; set; }

    /// <summary>JSON array of the ports (host == container, TCP) the broker published for this sandbox —
    /// e.g. "[51234,51235,51236,51237,51238]". Set once when the sandbox is first created and passed back
    /// as CreateSandboxRequest.PreferredPorts on any later recreate, so a container recreated after being
    /// orphaned keeps the same ports rather than handing the model a different set mid-session.</summary>
    public string PortsJson { get; set; } = "[]";

    public SandboxInstanceStatus Status { get; set; } = SandboxInstanceStatus.Creating;
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? DestroyedAt { get; set; }
}
