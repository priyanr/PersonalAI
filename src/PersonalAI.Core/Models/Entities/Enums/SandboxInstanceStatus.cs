namespace PersonalAI.Core.Models.Entities.Enums;

public enum SandboxInstanceStatus
{
    /// <summary>Create has been requested of the broker but no CreateSandboxResponse has come back yet.</summary>
    Creating,

    /// <summary>The broker confirmed the container exists and is running.</summary>
    Running,

    /// <summary>The DB row survived but the broker's container list no longer includes it (broker
    /// restart, manual `docker rm`, host reboot). The files are still on disk — the next tool call
    /// transparently creates a fresh container re-binding the same owner directory.</summary>
    Orphaned,

    /// <summary>Torn down deliberately (idle reaper, ephemeral run finished, session deleted).</summary>
    Destroyed
}
