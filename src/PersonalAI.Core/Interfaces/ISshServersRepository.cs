using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

public interface ISshServersRepository
{
    Task<IReadOnlyList<SshServerGroup>> GetGroupsAsync(CancellationToken ct);
    Task<SshServerGroup?> GetGroupAsync(Guid id, CancellationToken ct);
    /// <summary>Throws <see cref="InvalidOperationException"/> if the name is taken.</summary>
    Task<SshServerGroup> CreateGroupAsync(string name, string? description, CancellationToken ct);
    Task<SshServerGroup?> UpdateGroupAsync(Guid id, string name, string? description, CancellationToken ct);
    /// <summary>Also deletes the group's servers.</summary>
    Task<bool> DeleteGroupAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<SshServer>> GetServersAsync(Guid? groupId, CancellationToken ct);
    Task<SshServer?> GetServerAsync(Guid id, CancellationToken ct);
    /// <summary>Returns null if the group doesn't exist; throws <see cref="InvalidOperationException"/> if the name is taken in the group.</summary>
    Task<SshServer?> CreateServerAsync(Guid groupId, string name, string address, int port, string username, string secretRef, bool enabled, CancellationToken ct);
    /// <summary>Returns null if the server or group doesn't exist. Changing address, port or username clears the pinned host key.</summary>
    Task<SshServer?> UpdateServerAsync(Guid id, Guid groupId, string name, string address, int port, string username, string secretRef, bool enabled, CancellationToken ct);
    Task<bool> DeleteServerAsync(Guid id, CancellationToken ct);
    /// <summary>Clears the pinned host key (e.g. after a legitimate server rebuild).</summary>
    Task<bool> ResetHostKeyAsync(Guid id, CancellationToken ct);

    /// <summary>Records a connection attempt's outcome; pins <paramref name="pinFingerprint"/> if the server has none yet.</summary>
    Task RecordConnectionAsync(Guid id, bool success, string? error, string? pinFingerprint, CancellationToken ct);
}
