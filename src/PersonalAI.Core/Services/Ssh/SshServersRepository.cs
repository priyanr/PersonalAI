using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Ssh;

public class SshServersRepository(AppDbContext db) : ISshServersRepository
{
    public async Task<IReadOnlyList<SshServerGroup>> GetGroupsAsync(CancellationToken ct) =>
        await db.SshServerGroups.OrderBy(g => g.Name).ToListAsync(ct);

    public async Task<SshServerGroup?> GetGroupAsync(Guid id, CancellationToken ct) =>
        await db.SshServerGroups.FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<SshServerGroup> CreateGroupAsync(string name, string? description, CancellationToken ct)
    {
        if (await db.SshServerGroups.AnyAsync(g => g.Name == name, ct))
            throw new InvalidOperationException($"A group named '{name}' already exists.");

        var group = new SshServerGroup { Name = name, Description = description };
        db.SshServerGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<SshServerGroup?> UpdateGroupAsync(Guid id, string name, string? description, CancellationToken ct)
    {
        var group = await db.SshServerGroups.FindAsync([id], ct);
        if (group is null) return null;
        if (await db.SshServerGroups.AnyAsync(g => g.Name == name && g.Id != id, ct))
            throw new InvalidOperationException($"A group named '{name}' already exists.");

        group.Name = name;
        group.Description = description;
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<bool> DeleteGroupAsync(Guid id, CancellationToken ct)
    {
        var group = await db.SshServerGroups.Include(g => g.Servers).FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return false;

        db.SshServers.RemoveRange(group.Servers);
        db.SshServerGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<SshServer>> GetServersAsync(Guid? groupId, CancellationToken ct)
    {
        var query = db.SshServers.AsQueryable();
        if (groupId is not null) query = query.Where(t => t.GroupId == groupId);
        return await query.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<SshServer?> GetServerAsync(Guid id, CancellationToken ct) =>
        await db.SshServers.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<SshServer?> CreateServerAsync(
        Guid groupId, string name, string address, int port, string username, string secretRef, bool enabled, CancellationToken ct)
    {
        if (!await db.SshServerGroups.AnyAsync(g => g.Id == groupId, ct)) return null;
        if (await db.SshServers.AnyAsync(t => t.GroupId == groupId && t.Name == name, ct))
            throw new InvalidOperationException($"A server named '{name}' already exists in this group.");

        var server = new SshServer
        {
            GroupId = groupId, Name = name, Address = address, Port = port,
            Username = username, SecretRef = secretRef, Enabled = enabled
        };
        db.SshServers.Add(server);
        await db.SaveChangesAsync(ct);
        return server;
    }

    public async Task<SshServer?> UpdateServerAsync(
        Guid id, Guid groupId, string name, string address, int port, string username, string secretRef, bool enabled, CancellationToken ct)
    {
        var server = await db.SshServers.FindAsync([id], ct);
        if (server is null) return null;
        if (!await db.SshServerGroups.AnyAsync(g => g.Id == groupId, ct)) return null;
        if (await db.SshServers.AnyAsync(t => t.GroupId == groupId && t.Name == name && t.Id != id, ct))
            throw new InvalidOperationException($"A server named '{name}' already exists in this group.");

        // A different endpoint or identity is a different machine as far as the pinned host key goes.
        if (server.Address != address || server.Port != port || server.Username != username)
            server.HostKeyFingerprint = null;

        server.GroupId = groupId;
        server.Name = name;
        server.Address = address;
        server.Port = port;
        server.Username = username;
        server.SecretRef = secretRef;
        server.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        return server;
    }

    public async Task<bool> DeleteServerAsync(Guid id, CancellationToken ct)
    {
        var server = await db.SshServers.FindAsync([id], ct);
        if (server is null) return false;

        db.SshServers.Remove(server);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ResetHostKeyAsync(Guid id, CancellationToken ct)
    {
        var server = await db.SshServers.FindAsync([id], ct);
        if (server is null) return false;

        server.HostKeyFingerprint = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task RecordConnectionAsync(Guid id, bool success, string? error, string? pinFingerprint, CancellationToken ct)
    {
        var server = await db.SshServers.FindAsync([id], ct);
        if (server is null) return;

        if (success)
        {
            server.LastSeenAt = DateTimeOffset.UtcNow;
            server.LastError = null;
        }
        else
        {
            server.LastError = error;
        }
        if (pinFingerprint is not null && server.HostKeyFingerprint is null)
            server.HostKeyFingerprint = pinFingerprint;
        await db.SaveChangesAsync(ct);
    }
}
