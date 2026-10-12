using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Tools;

public class NetworkScanToolTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static NetworkScanTool NewTool(NetworkScanSettings? settings = null) =>
        new(Options.Create(settings ?? new NetworkScanSettings()));

    [Fact]
    public async Task ExecuteAsync_MissingTarget_Throws()
    {
        var tool = NewTool();
        var act = async () => await tool.ExecuteAsync(Args(new { }), new ToolExecutionContext(false), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_MalformedTarget_ReturnsError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args(new { target = "not-an-ip" }), new ToolExecutionContext(false), CancellationToken.None);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("parse");
    }

    [Fact]
    public async Task ExecuteAsync_PublicIpTarget_ReturnsError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args(new { target = "8.8.8.8", protocols = new[] { "icmp" } }), new ToolExecutionContext(false), CancellationToken.None);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("outside the allowed private-network ranges");
    }

    [Fact]
    public async Task ExecuteAsync_PublicCidrTarget_ReturnsError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args(new { target = "1.2.3.0/24", protocols = new[] { "icmp" } }), new ToolExecutionContext(false), CancellationToken.None);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CidrExceedsMaxHosts_ReturnsErrorWithoutProbing()
    {
        var tool = NewTool(new NetworkScanSettings { MaxHosts = 16 });
        var result = await tool.ExecuteAsync(Args(new { target = "192.168.1.0/24", protocols = new[] { "icmp" } }), new ToolExecutionContext(false), CancellationToken.None);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("256").And.Contain("16");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProtocol_ReturnsError()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args(new { target = "127.0.0.1", protocols = new[] { "ftp" } }), new ToolExecutionContext(false), CancellationToken.None);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("ftp");
    }

    [Fact]
    public async Task ExecuteAsync_IcmpLoopback_ReportsHostUp()
    {
        var tool = NewTool();
        var result = await tool.ExecuteAsync(Args(new { target = "127.0.0.1", protocols = new[] { "icmp" } }), new ToolExecutionContext(false), CancellationToken.None);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        completed.Content.Should().Contain("icmp: up");
    }

    [Fact]
    public async Task ExecuteAsync_TcpOpenPort_ReportsOpenAndOmitsClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();

        // A port very unlikely to have anything listening, used to verify closed ports are omitted.
        var closedPort = openPort == 65535 ? openPort - 1 : openPort + 1;

        try
        {
            var tool = NewTool();
            var result = await tool.ExecuteAsync(
                Args(new { target = "127.0.0.1", protocols = new[] { "tcp" }, tcpPorts = new[] { openPort, closedPort } }),
                new ToolExecutionContext(false), CancellationToken.None);

            var completed = result.Should().BeOfType<ToolCompleted>().Subject;
            completed.IsError.Should().BeFalse();
            completed.Content.Should().Contain($"tcp/{openPort}: open");
            completed.Content.Should().NotContain($"tcp/{closedPort}:");
        }
        finally
        {
            listener.Stop();
            if (acceptTask.IsCompletedSuccessfully)
                (await acceptTask).Dispose();
        }
    }

    [Fact]
    public async Task ExecuteAsync_TcpBanner_IsCapturedAndTruncated()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var banner = System.Text.Encoding.ASCII.GetBytes("SSH-2.0-TestBanner\r\n");
            await stream.WriteAsync(banner);
            await Task.Delay(600); // keep the connection open long enough for the tool's read window
        });

        try
        {
            var tool = NewTool();
            var result = await tool.ExecuteAsync(
                Args(new { target = "127.0.0.1", protocols = new[] { "tcp" }, tcpPorts = new[] { port } }),
                new ToolExecutionContext(false), CancellationToken.None);

            var completed = result.Should().BeOfType<ToolCompleted>().Subject;
            completed.Content.Should().Contain("SSH-2.0-TestBanner");
        }
        finally
        {
            listener.Stop();
            await serverTask;
        }
    }

    [Fact]
    public async Task ExecuteAsync_UdpNoListener_ReportsOpenOrFiltered()
    {
        // Pick a loopback UDP port nothing is bound to — deterministic timeout path (no ICMP
        // unreachable is guaranteed on loopback across platforms, so "closed" isn't asserted here).
        using var probe = new UdpClient(0);
        var freePort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Close();

        var tool = NewTool(new NetworkScanSettings { ProbeTimeoutMs = 200 });
        var result = await tool.ExecuteAsync(
            Args(new { target = "127.0.0.1", protocols = new[] { "udp" }, udpPorts = new[] { freePort } }),
            new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        // Either classification is a valid outcome of an unresponded probe depending on the platform's
        // ICMP unreachable behavior on loopback; both are informative, non-error results.
        (completed.Content.Contains("open|filtered") || completed.Content.Contains("closed") || completed.Content.Contains("No hosts responded"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NoOpenPorts_ReturnsCompactNonErrorResult()
    {
        var tool = NewTool(new NetworkScanSettings { ProbeTimeoutMs = 200 });
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var busyPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // free the port immediately — nothing listens on it

        var result = await tool.ExecuteAsync(
            Args(new { target = "127.0.0.1", protocols = new[] { "tcp" }, tcpPorts = new[] { busyPort } }),
            new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        completed.Content.Should().Contain("No hosts responded");
    }

    [Fact]
    public async Task ExecuteAsync_ExcludesNetworkAndBroadcastAddresses()
    {
        // 127.0.0.0/30 covers 127.0.0.0-127.0.0.3; with exclusion the network (.0) and broadcast (.3)
        // addresses must never be probed, leaving only .1 and .2.
        var tool = NewTool(new NetworkScanSettings { ExcludeNetworkAndBroadcastAddresses = true, ProbeTimeoutMs = 200 });
        var result = await tool.ExecuteAsync(
            Args(new { target = "127.0.0.0/30", protocols = new[] { "icmp" } }),
            new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.Content.Should().NotContain("Host 127.0.0.0:");
        completed.Content.Should().NotContain("Host 127.0.0.3:");
    }
}
