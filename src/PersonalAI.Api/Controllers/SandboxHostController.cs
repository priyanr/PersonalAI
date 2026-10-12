using System.Net;
using System.Security.Cryptography;
using System.Text;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace PersonalAI.Api.Controllers;

/// <summary>The WebSocket upgrade PersonalAI.SandboxHost dials outbound into, plus a status endpoint for
/// the frontend. The upgrade action authenticates with a shared host key rather than a user bearer token
/// — the broker has no user identity to present — so it must stay reachable even when Program.cs has
/// registered the global AuthorizeFilter (Auth:Enabled), the same reasoning GmailOAuthController's
/// callback and AuthController's bootstrap actions already document for their own [AllowAnonymous] use.</summary>
[ApiController]
[Route("api/sandbox")]
public class SandboxHostController(
    SandboxHostRegistry registry,
    ISandboxRepository sandboxRepository,
    SandboxProvisioningService provisioning,
    IOptions<SandboxSettings> settings) : ControllerBase
{
    private const string HostKeyHeader = "X-Sandbox-Host-Key";

    /// <summary>[AllowAnonymous] is safe here only because every check below (Sandbox:Enabled, a
    /// configured HostKey, a constant-time comparison of the presented key) runs to completion before
    /// AcceptWebSocketAsync — a wrong or missing key never reaches the registry, and never gets a socket
    /// upgraded out from under it.</summary>
    [AllowAnonymous]
    [HttpGet("host")]
    public async Task Host(CancellationToken ct)
    {
        if (!IsAuthorized())
        {
            HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        // HttpContext.Connection.RemoteIpAddress already reflects a trusted proxy's X-Forwarded-For
        // (Program.cs's UseForwardedHeaders runs ahead of every controller) — this is the address a
        // published sandbox port is actually reachable at from outside the container, surfaced to the
        // model by SandboxTool's "ports" action.
        var remoteIp = NormalizeRemoteIp(HttpContext.Connection.RemoteIpAddress);

        // Must await the connection's full lifetime here: returning early disposes the just-accepted
        // WebSocket out from under the broker instead of keeping the dial-in connection alive.
        await registry.HandleConnectionAsync(socket, remoteIp, ct);
    }

    /// <summary>A dual-stack socket often reports an IPv4 client as an IPv4-mapped IPv6 address
    /// ("::ffff:172.17.0.1") — mapped back down here so what gets stored and eventually put in a URL is
    /// the plain, unambiguous IPv4 form rather than confusing IPv6 syntax. Public and static purely so
    /// this one piece of real logic is unit-testable without a WebSocket handshake.</summary>
    public static string? NormalizeRemoteIp(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4().ToString() : address?.ToString();

    [HttpGet("status")]
    public async Task<ActionResult<SandboxStatusDto>> Status(CancellationToken ct)
    {
        var active = await sandboxRepository.GetAllActiveAsync(ct);
        var running = active.Count(s => s.Status == SandboxInstanceStatus.Running);
        return Ok(new SandboxStatusDto(registry.IsConnected, registry.HostId, registry.ConnectedAt, running, registry.RemoteIp));
    }

    /// <summary>What the frontend polls to decide whether to show the "Sandbox files" button for a chat
    /// session or agent run. A pure read (CheckBrowsableAsync never creates or recreates a container) so
    /// polling this on every page load/tick never causes container churn — the actual get-or-create only
    /// happens when the sandbox file browser's own endpoints are used.</summary>
    [HttpGet("{ownerId:guid}/status")]
    public async Task<ActionResult<SandboxOwnerStatusDto>> OwnerStatus(Guid ownerId, CancellationToken ct)
    {
        var (canBrowse, status, ports) = await provisioning.CheckBrowsableAsync(ownerId, ct);
        return Ok(new SandboxOwnerStatusDto(registry.IsConnected && canBrowse, status, ports));
    }

    private bool IsAuthorized()
    {
        var config = settings.Value;
        if (!config.Enabled || string.IsNullOrEmpty(config.HostKey))
            return false;

        var presented = Request.Headers[HostKeyHeader].ToString();
        if (string.IsNullOrEmpty(presented))
            return false;

        // CryptographicOperations.FixedTimeEquals itself short-circuits (non-constant-time) when the two
        // spans differ in length, which would leak the expected key's length to a probing attacker.
        // Hashing both sides to a fixed 32 bytes first removes that signal before the constant-time
        // compare runs.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(config.HostKey));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
