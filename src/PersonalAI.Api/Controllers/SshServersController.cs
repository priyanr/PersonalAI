using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Ssh;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/ssh")]
public class SshServersController(SshServersService ssh) : ControllerBase
{
    [HttpGet("server-groups")]
    public async Task<List<SshServerGroupDto>> GetGroups(CancellationToken ct)
    {
        return await ssh.GetGroups(ct);
    }

    [HttpPost("server-groups")]
    public async Task<SshServerGroupDto> CreateGroup(SaveSshServerGroupRequest request, CancellationToken ct)
    {
        return await ssh.CreateGroup(request, ct);
    }

    [HttpPatch("server-groups/{id:guid}")]
    public async Task<SshServerGroupDto> UpdateGroup(Guid id, SaveSshServerGroupRequest request, CancellationToken ct)
    {
        return await ssh.UpdateGroup(id, request, ct);
    }

    [HttpDelete("server-groups/{id:guid}")]
    public async Task<IActionResult> DeleteGroup(Guid id, CancellationToken ct)
    {
        await ssh.DeleteGroup(id, ct);
        return NoContent();
    }

    [HttpGet("servers")]
    public async Task<List<SshServerDto>> GetServers([FromQuery] Guid? groupId, CancellationToken ct)
    {
        return await ssh.GetServers(groupId, ct);
    }

    [HttpGet("servers/{id:guid}")]
    public async Task<SshServerDto> GetServer(Guid id, CancellationToken ct)
    {
        return await ssh.GetServer(id, ct);
    }

    [HttpPost("servers")]
    public async Task<SshServerDto> CreateServer(SaveSshServerRequest request, CancellationToken ct)
    {
        return await ssh.CreateServer(request, ct);
    }

    [HttpPatch("servers/{id:guid}")]
    public async Task<SshServerDto> UpdateServer(Guid id, SaveSshServerRequest request, CancellationToken ct)
    {
        return await ssh.UpdateServer(id, request, ct);
    }

    [HttpDelete("servers/{id:guid}")]
    public async Task<IActionResult> DeleteServer(Guid id, CancellationToken ct)
    {
        await ssh.DeleteServer(id, ct);
        return NoContent();
    }

    [HttpPost("servers/{id:guid}/test-connection")]
    public async Task<SshServerTestResultDto> TestConnection(Guid id, CancellationToken ct)
    {
        return await ssh.TestConnection(id, ct);
    }

    [HttpPost("servers/{id:guid}/reset-host-key")]
    public async Task<IActionResult> ResetHostKey(Guid id, CancellationToken ct)
    {
        await ssh.ResetHostKey(id, ct);
        return NoContent();
    }
}
