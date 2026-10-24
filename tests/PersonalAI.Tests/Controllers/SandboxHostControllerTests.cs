using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PersonalAI.Api.Controllers;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Controllers;

public class SandboxHostControllerTests
{
    private static (SandboxHostController Controller, AppDbContext Db, SandboxHostRegistry Registry) CreateControllerWithDeps(
        SandboxSettings settings, string? presentedHostKey)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ISandboxRepository, SandboxRepository>();
        var provider = services.BuildServiceProvider();

        var registry = new SandboxHostRegistry(
            Options.Create(settings), provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeTimeProvider(DateTimeOffset.UtcNow), Mock.Of<ILogger<SandboxHostRegistry>>());

        var repo = provider.GetRequiredService<ISandboxRepository>();
        var db = provider.GetRequiredService<AppDbContext>();
        var provisioning = new SandboxProvisioningService(db, registry, repo);
        var controller = new SandboxHostController(registry, repo, provisioning, Options.Create(settings));

        var httpContext = new DefaultHttpContext();
        if (presentedHostKey is not null)
            httpContext.Request.Headers["X-Sandbox-Host-Key"] = presentedHostKey;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, db, registry);
    }

    private static SandboxHostController CreateController(SandboxSettings settings, string? presentedHostKey) =>
        CreateControllerWithDeps(settings, presentedHostKey).Controller;

    private static SandboxSettings ValidSettings() => new() { Enabled = true, HostKey = "the-real-secret" };

    [Fact]
    public async Task Host_MissingHostKeyHeader_Returns401AndDoesNotUpgrade()
    {
        var controller = CreateController(ValidSettings(), presentedHostKey: null);

        await controller.Host(CancellationToken.None);

        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        controller.HttpContext.WebSockets.IsWebSocketRequest.Should().BeFalse();
    }

    [Fact]
    public async Task Host_WrongHostKeyHeader_Returns401()
    {
        var controller = CreateController(ValidSettings(), presentedHostKey: "not-the-secret");

        await controller.Host(CancellationToken.None);

        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Host_WrongHostKeyOfDifferentLength_Returns401()
    {
        // Exercises the length-mismatch path through FixedTimeEquals specifically (see the hashing
        // comment in SandboxHostController.IsAuthorized) — a much shorter/longer guess must still 401.
        var controller = CreateController(ValidSettings(), presentedHostKey: "x");

        await controller.Host(CancellationToken.None);

        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Host_SandboxDisabled_Returns401EvenWithCorrectKey()
    {
        var settings = ValidSettings();
        settings.Enabled = false;
        var controller = CreateController(settings, presentedHostKey: settings.HostKey);

        await controller.Host(CancellationToken.None);

        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Host_NoHostKeyConfigured_Returns401EvenIfHeaderPresent()
    {
        var settings = new SandboxSettings { Enabled = true, HostKey = "" };
        var controller = CreateController(settings, presentedHostKey: "anything");

        await controller.Host(CancellationToken.None);

        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Host_CorrectKeyButNotAWebSocketRequest_Returns400()
    {
        var settings = ValidSettings();
        var controller = CreateController(settings, presentedHostKey: settings.HostKey);

        await controller.Host(CancellationToken.None);

        // DefaultHttpContext has no IHttpWebSocketFeature, so IsWebSocketRequest is false and the plain
        // GET falls through to the 400 branch instead of ever calling AcceptWebSocketAsync.
        controller.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Status_ReturnsDisconnectedWhenNoRegistryConnection()
    {
        var controller = CreateController(ValidSettings(), presentedHostKey: null);

        var result = await controller.Status(CancellationToken.None);

        var dto = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PersonalAI.Core.Models.DTOs.SandboxStatusDto>().Subject;
        dto.Connected.Should().BeFalse();
        dto.HostId.Should().BeNull();
        dto.ActiveSandboxes.Should().Be(0);
        dto.RemoteIp.Should().BeNull();
    }

    [Fact]
    public async Task OwnerStatus_NoBrokerConnected_ReturnsCanBrowseFalse()
    {
        var (controller, _, _) = CreateControllerWithDeps(ValidSettings(), presentedHostKey: null);

        var result = await controller.OwnerStatus(Guid.NewGuid(), CancellationToken.None);

        var dto = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PersonalAI.Core.Models.DTOs.SandboxOwnerStatusDto>().Subject;
        dto.CanBrowse.Should().BeFalse();
    }

    [Fact]
    public async Task OwnerStatus_BrokerConnectedButNoSandboxForOwner_ReturnsCanBrowseFalse()
    {
        var (controller, _, registry) = CreateControllerWithDeps(ValidSettings(), presentedHostKey: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ConnectBroker(registry, cts.Token);
        await WaitUntilAsync(() => registry.IsConnected);

        var result = await controller.OwnerStatus(Guid.NewGuid(), CancellationToken.None);

        var dto = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PersonalAI.Core.Models.DTOs.SandboxOwnerStatusDto>().Subject;
        dto.CanBrowse.Should().BeFalse();
        cts.Cancel();
    }

    [Fact]
    public async Task OwnerStatus_BrokerConnectedAndSessionSandboxExists_ReturnsCanBrowseTrue()
    {
        var (controller, db, registry) = CreateControllerWithDeps(ValidSettings(), presentedHostKey: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ConnectBroker(registry, cts.Token);
        await WaitUntilAsync(() => registry.IsConnected);

        var ownerId = Guid.NewGuid();
        await TestSeed.SandboxAsync(db, new SandboxInstance
        {
            Kind = SandboxKind.Session,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Destroyed,
            PortsJson = "[]"
        }, CancellationToken.None);

        var result = await controller.OwnerStatus(ownerId, CancellationToken.None);

        var dto = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PersonalAI.Core.Models.DTOs.SandboxOwnerStatusDto>().Subject;
        dto.CanBrowse.Should().BeTrue("a chat session's Destroyed row still has its files kept");
        cts.Cancel();
    }

    [Fact]
    public async Task OwnerStatus_EphemeralSandboxDestroyed_ReturnsCanBrowseFalse()
    {
        var (controller, db, registry) = CreateControllerWithDeps(ValidSettings(), presentedHostKey: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ConnectBroker(registry, cts.Token);
        await WaitUntilAsync(() => registry.IsConnected);

        var ownerId = Guid.NewGuid();
        await TestSeed.SandboxAsync(db, new SandboxInstance
        {
            Kind = SandboxKind.Ephemeral,
            OwnerId = ownerId,
            Image = "python:3.12-slim",
            Status = SandboxInstanceStatus.Destroyed,
            PortsJson = "[]"
        }, CancellationToken.None);

        var result = await controller.OwnerStatus(ownerId, CancellationToken.None);

        var dto = ((OkObjectResult)result.Result!).Value.Should().BeOfType<PersonalAI.Core.Models.DTOs.SandboxOwnerStatusDto>().Subject;
        dto.CanBrowse.Should().BeFalse("an ephemeral run's files are deleted once it's torn down");
        cts.Cancel();
    }

    /// <summary>Minimal handshake to get registry.IsConnected == true: pushes HostHello and answers the
    /// reconciliation ListSandboxesRequest and any Heartbeat so the connection doesn't time itself out
    /// mid-test — mirrors SandboxHostRegistryTests' own ConnectBroker helper, kept local here rather than
    /// shared since these tests don't need onOtherFrame/remoteIp.</summary>
    private static Tests.Sandbox.FakeFrameTransport ConnectBroker(SandboxHostRegistry registry, CancellationToken ct)
    {
        var transport = new Tests.Sandbox.FakeFrameTransport();
        transport.Push(new HostHello(Guid.NewGuid(), "host-1", "24.0.5", 1));
        transport.OnSend = frame =>
        {
            switch (frame)
            {
                case ListSandboxesRequest lsr:
                    transport.Push(new ListSandboxesResponse(lsr.RequestId, []));
                    break;
                case Heartbeat hb:
                    transport.Push(new Heartbeat(hb.RequestId, DateTimeOffset.UtcNow));
                    break;
            }
            return Task.CompletedTask;
        };

        _ = registry.RunConnectionAsync(transport, ct);
        return transport;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(15);
        }
    }

    [Fact]
    public void NormalizeRemoteIp_NullAddress_ReturnsNull()
    {
        SandboxHostController.NormalizeRemoteIp(null).Should().BeNull();
    }

    [Fact]
    public void NormalizeRemoteIp_PlainIPv4_ReturnedAsIs()
    {
        var address = System.Net.IPAddress.Parse("172.17.0.5");

        SandboxHostController.NormalizeRemoteIp(address).Should().Be("172.17.0.5");
    }

    [Fact]
    public void NormalizeRemoteIp_IPv4MappedToIPv6_MapsDownToPlainIPv4()
    {
        // What a dual-stack socket commonly reports for an IPv4 client -- must come out as the plain
        // dotted form, not the confusing "::ffff:..." IPv6 syntax, since this ends up as the host half of
        // a "http://host:port" URL handed to the model.
        var address = System.Net.IPAddress.Parse("::ffff:172.17.0.5");

        SandboxHostController.NormalizeRemoteIp(address).Should().Be("172.17.0.5");
    }

    [Fact]
    public void NormalizeRemoteIp_GenuineIPv6_ReturnedUnbracketed()
    {
        // Bracketing for URL use is SandboxTool's job (it's the one building the URL), not this
        // controller-level normalization step.
        var address = System.Net.IPAddress.Parse("2001:db8::1");

        SandboxHostController.NormalizeRemoteIp(address).Should().Be("2001:db8::1");
    }
}
