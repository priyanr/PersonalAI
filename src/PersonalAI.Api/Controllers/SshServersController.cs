using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Ssh;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/ssh")]
public class SshServersController(ISshServersRepository repo, ISecretsRepository secrets, SshConnectionService connection) : ControllerBase
{
    [HttpGet("server-groups")]
    public async Task<ActionResult<IReadOnlyList<SshServerGroupDto>>> GetGroups(CancellationToken ct)
    {
        var groups = await repo.GetGroupsAsync(ct);
        var servers = await repo.GetServersAsync(null, ct);
        var counts = servers.GroupBy(t => t.GroupId).ToDictionary(g => g.Key, g => g.Count());
        return Ok(groups.Select(g => ToDto(g, counts.GetValueOrDefault(g.Id))).ToList());
    }

    [HttpPost("server-groups")]
    public async Task<ActionResult<SshServerGroupDto>> CreateGroup(SaveSshServerGroupRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { message = "Name is required." });

        try
        {
            return Ok(ToDto(await repo.CreateGroupAsync(name, Clean(request.Description), ct), 0));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPatch("server-groups/{id:guid}")]
    public async Task<ActionResult<SshServerGroupDto>> UpdateGroup(Guid id, SaveSshServerGroupRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) return BadRequest(new { message = "Name is required." });

        try
        {
            var group = await repo.UpdateGroupAsync(id, name, Clean(request.Description), ct);
            if (group is null) return NotFound();
            return Ok(ToDto(group, (await repo.GetServersAsync(id, ct)).Count));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("server-groups/{id:guid}")]
    public async Task<IActionResult> DeleteGroup(Guid id, CancellationToken ct) =>
        await repo.DeleteGroupAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("servers")]
    public async Task<ActionResult<IReadOnlyList<SshServerDto>>> GetServers([FromQuery] Guid? groupId, CancellationToken ct) =>
        Ok((await repo.GetServersAsync(groupId, ct)).Select(ToDto).ToList());

    [HttpGet("servers/{id:guid}")]
    public async Task<ActionResult<SshServerDto>> GetServer(Guid id, CancellationToken ct)
    {
        var server = await repo.GetServerAsync(id, ct);
        return server is null ? NotFound() : Ok(ToDto(server));
    }

    [HttpPost("servers")]
    public async Task<ActionResult<SshServerDto>> CreateServer(SaveSshServerRequest request, CancellationToken ct)
    {
        var error = await ValidateAsync(request, ct);
        if (error is not null) return BadRequest(new { message = error });

        try
        {
            var server = await repo.CreateServerAsync(
                request.GroupId, request.Name.Trim(), request.Address.Trim(), request.Port ?? 22,
                request.Username.Trim(), request.SecretRef.Trim(), request.Enabled, ct);
            return server is null ? BadRequest(new { message = "Group not found." }) : Ok(ToDto(server));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPatch("servers/{id:guid}")]
    public async Task<ActionResult<SshServerDto>> UpdateServer(Guid id, SaveSshServerRequest request, CancellationToken ct)
    {
        var error = await ValidateAsync(request, ct);
        if (error is not null) return BadRequest(new { message = error });

        try
        {
            var server = await repo.UpdateServerAsync(
                id, request.GroupId, request.Name.Trim(), request.Address.Trim(), request.Port ?? 22,
                request.Username.Trim(), request.SecretRef.Trim(), request.Enabled, ct);
            return server is null ? NotFound() : Ok(ToDto(server));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("servers/{id:guid}")]
    public async Task<IActionResult> DeleteServer(Guid id, CancellationToken ct) =>
        await repo.DeleteServerAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("servers/{id:guid}/test-connection")]
    public async Task<ActionResult<SshServerTestResultDto>> TestConnection(Guid id, CancellationToken ct)
    {
        var result = await connection.TestAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("servers/{id:guid}/reset-host-key")]
    public async Task<IActionResult> ResetHostKey(Guid id, CancellationToken ct) =>
        await repo.ResetHostKeyAsync(id, ct) ? NoContent() : NotFound();

    private async Task<string?> ValidateAsync(SaveSshServerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(request.Address)) return "Address is required.";
        if (string.IsNullOrWhiteSpace(request.Username)) return "Username is required.";
        if (string.IsNullOrWhiteSpace(request.SecretRef)) return "A secret holding the SSH key or password is required.";
        if (request.Port is < 1 or > 65535) return "Port must be between 1 and 65535.";
        if (!SshSecrets.IsAllowed(request.SecretRef.Trim())) return SshSecrets.NotAllowedMessage(request.SecretRef.Trim());
        if (await secrets.GetValueAsync(request.SecretRef.Trim(), ct) is null)
            return $"No secret named '{request.SecretRef.Trim()}'. Create it on the Secrets page first.";
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SshServerGroupDto ToDto(SshServerGroup g, int serverCount) =>
        new(g.Id, g.Name, g.Description, serverCount, g.CreatedAt);

    private static SshServerDto ToDto(SshServer t) => new(
        t.Id, t.GroupId, t.Name, t.Address, t.Port, t.Username, t.SecretRef, t.Enabled,
        t.HostKeyFingerprint, t.LastSeenAt, t.LastError, t.CreatedAt);
}
