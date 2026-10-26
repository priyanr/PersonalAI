using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Secrets;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Ssh;

/// <summary>Server groups and SSH servers (/api/ssh): create, edit, delete, test a connection and reset a pinned host key.</summary>
public class SshServersService(AppDbContext db, ISshServersRepository repo, ISecretsRepository secrets, ISshRunner ssh)
{
    // Fixed template; never built from user or model input.
    private const string ProbeCommand = "uname -sr";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<List<SshServerGroupDto>> GetGroups(CancellationToken ct)
    {
        var groups = await repo.GetGroupsAsync(ct);
        var servers = await repo.GetServersAsync(null, ct);
        var counts = servers.GroupBy(t => t.GroupId).ToDictionary(g => g.Key, g => g.Count());
        return groups.Select(g => ToDto(g, counts.GetValueOrDefault(g.Id))).ToList();
    }

    public async Task<SshServerGroupDto> CreateGroup(SaveSshServerGroupRequest request, CancellationToken ct)
    {
        var name = RequireGroupName(request);
        await EnsureGroupNameFree(name, null, ct);

        var group = new SshServerGroup { Name = name, Description = Clean(request.Description) };
        db.SshServerGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return ToDto(group, 0);
    }

    public async Task<SshServerGroupDto> UpdateGroup(Guid id, SaveSshServerGroupRequest request, CancellationToken ct)
    {
        var name = RequireGroupName(request);
        var group = await repo.GetGroupAsync(id, ct) ?? throw GroupNotFound(id);
        await EnsureGroupNameFree(name, id, ct);

        group.Name = name;
        group.Description = Clean(request.Description);
        await db.SaveChangesAsync(ct);
        return ToDto(group, (await repo.GetServersAsync(id, ct)).Count);
    }

    /// <summary>Also deletes the group's servers.</summary>
    public async Task DeleteGroup(Guid id, CancellationToken ct)
    {
        var group = await repo.GetGroupWithServersAsync(id, ct) ?? throw GroupNotFound(id);
        db.SshServers.RemoveRange(group.Servers);
        db.SshServerGroups.Remove(group);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SshServerDto>> GetServers(Guid? groupId, CancellationToken ct)
    {
        return (await repo.GetServersAsync(groupId, ct)).Select(ToDto).ToList();
    }

    public async Task<SshServerDto> GetServer(Guid id, CancellationToken ct)
    {
        return ToDto(await repo.GetServerAsync(id, ct) ?? throw ServerNotFound(id));
    }

    public async Task<SshServerDto> CreateServer(SaveSshServerRequest request, CancellationToken ct)
    {
        await ValidateServer(request, ct);
        if (await repo.GetGroupAsync(request.GroupId, ct) is null) throw new InvalidRequestException("Group not found.");
        var name = request.Name.Trim();
        await EnsureServerNameFree(request.GroupId, name, null, ct);

        var server = new SshServer
        {
            GroupId = request.GroupId, Name = name, Address = request.Address.Trim(), Port = request.Port ?? 22,
            Username = request.Username.Trim(), SecretRef = request.SecretRef.Trim(), Enabled = request.Enabled
        };
        db.SshServers.Add(server);
        await db.SaveChangesAsync(ct);
        return ToDto(server);
    }

    /// <summary>Changing address, port or username clears the pinned host key.</summary>
    public async Task<SshServerDto> UpdateServer(Guid id, SaveSshServerRequest request, CancellationToken ct)
    {
        await ValidateServer(request, ct);
        var server = await repo.GetServerAsync(id, ct) ?? throw ServerNotFound(id);
        if (await repo.GetGroupAsync(request.GroupId, ct) is null) throw GroupNotFound(request.GroupId);
        var name = request.Name.Trim();
        await EnsureServerNameFree(request.GroupId, name, id, ct);

        var address = request.Address.Trim();
        var port = request.Port ?? 22;
        var username = request.Username.Trim();
        // A different endpoint or identity is a different machine as far as the pinned host key goes.
        if (server.Address != address || server.Port != port || server.Username != username)
            server.HostKeyFingerprint = null;

        server.GroupId = request.GroupId;
        server.Name = name;
        server.Address = address;
        server.Port = port;
        server.Username = username;
        server.SecretRef = request.SecretRef.Trim();
        server.Enabled = request.Enabled;
        await db.SaveChangesAsync(ct);
        return ToDto(server);
    }

    public async Task DeleteServer(Guid id, CancellationToken ct)
    {
        var server = await repo.GetServerAsync(id, ct) ?? throw ServerNotFound(id);
        db.SshServers.Remove(server);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Runs a harmless probe, records the outcome on the server, and pins the host key on first success.</summary>
    public async Task<SshServerTestResultDto> TestConnection(Guid id, CancellationToken ct)
    {
        var server = await repo.GetServerAsync(id, ct) ?? throw ServerNotFound(id);

        var result = await ssh.RunAsync(server, ProbeCommand, ProbeTimeout, ct);
        var ok = result.Connected && result.Error is null && result.ExitCode == 0;
        var error = ok ? null : result.Error ?? $"Probe exited with code {result.ExitCode}: {result.Stderr.Trim()}";

        server.RecordConnection(ok, error, ok ? result.HostKeyFingerprint : null);
        await db.SaveChangesAsync(ct);
        return new SshServerTestResultDto(ok, ok ? result.Stdout.Trim() : null, error, result.HostKeyFingerprint);
    }

    /// <summary>Clears the pinned host key (e.g. after a legitimate server rebuild).</summary>
    public async Task ResetHostKey(Guid id, CancellationToken ct)
    {
        var server = await repo.GetServerAsync(id, ct) ?? throw ServerNotFound(id);
        server.HostKeyFingerprint = null;
        await db.SaveChangesAsync(ct);
    }

    private static string RequireGroupName(SaveSshServerGroupRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) throw new InvalidRequestException("Name is required.");
        return name;
    }

    private async Task EnsureGroupNameFree(string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.SshServerGroups.AnyAsync(g => g.Name == name && g.Id != exceptId, ct))
            throw new ConflictException($"A group named '{name}' already exists.");
    }

    private async Task EnsureServerNameFree(Guid groupId, string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.SshServers.AnyAsync(t => t.GroupId == groupId && t.Name == name && t.Id != exceptId, ct))
            throw new ConflictException($"A server named '{name}' already exists in this group.");
    }

    private async Task ValidateServer(SaveSshServerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidRequestException("Name is required.");
        if (string.IsNullOrWhiteSpace(request.Address)) throw new InvalidRequestException("Address is required.");
        if (string.IsNullOrWhiteSpace(request.Username)) throw new InvalidRequestException("Username is required.");
        if (string.IsNullOrWhiteSpace(request.SecretRef)) throw new InvalidRequestException("A secret holding the SSH key or password is required.");
        if (request.Port is < 1 or > 65535) throw new InvalidRequestException("Port must be between 1 and 65535.");
        var secretRef = request.SecretRef.Trim();
        if (!SshSecrets.IsAllowed(secretRef)) throw new InvalidRequestException(SshSecrets.NotAllowedMessage(secretRef));
        if (await secrets.GetValueAsync(secretRef, ct) is null)
            throw new InvalidRequestException($"No secret named '{secretRef}'. Create it on the Secrets page first.");
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static NotFoundException GroupNotFound(Guid id)
    {
        return new NotFoundException($"Server group {id} not found.");
    }

    private static NotFoundException ServerNotFound(Guid id)
    {
        return new NotFoundException($"Server {id} not found.");
    }

    private static SshServerGroupDto ToDto(SshServerGroup g, int serverCount)
    {
        return new SshServerGroupDto(g.Id, g.Name, g.Description, serverCount, g.CreatedAt);
    }

    private static SshServerDto ToDto(SshServer t)
    {
        return new SshServerDto(
            t.Id, t.GroupId, t.Name, t.Address, t.Port, t.Username, t.SecretRef, t.Enabled,
            t.HostKeyFingerprint, t.LastSeenAt, t.LastError, t.CreatedAt);
    }
}
