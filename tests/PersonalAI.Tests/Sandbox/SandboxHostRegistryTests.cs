using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Sandbox;

public class SandboxHostRegistryTests
{
    private static (SandboxHostRegistry Registry, FakeTimeProvider Clock) CreateRegistry(SandboxSettings? settings = null)
    {
        // A real (InMemory) AppDbContext behind a real IServiceScopeFactory, exactly what ReconcileAsync
        // needs — the registry is a singleton but ISandboxRepository is Scoped, so it must resolve it
        // through a fresh scope on every (re)connect rather than taking a constructor dependency on it.
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<ISandboxRepository, PersonalAI.Core.Services.Sandbox.SandboxRepository>();
        var provider = services.BuildServiceProvider();

        var options = Options.Create(settings ?? new SandboxSettings
        {
            HeartbeatIntervalSeconds = 1,
            HeartbeatTimeoutSeconds = 1,
            RequestTimeoutSeconds = 5,
            ImagePullTimeoutSeconds = 5,
            MaxOutputCharsPerStream = 3000
        });

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new SandboxHostRegistry(
            options, provider.GetRequiredService<IServiceScopeFactory>(), clock, Mock.Of<ILogger<SandboxHostRegistry>>());
        return (registry, clock);
    }

    /// <summary>Pushes HostHello, wires the default responses every connection needs (ListSandboxes during
    /// reconciliation, Heartbeat echoes so a short HeartbeatIntervalSeconds doesn't time the connection out
    /// mid-test), and fires RunConnectionAsync in the background. <paramref name="onOtherFrame"/> lets a
    /// test answer additional request types (e.g. ExecRequest) without losing those defaults.</summary>
    private static FakeFrameTransport ConnectBroker(
        SandboxHostRegistry registry, string hostId, CancellationToken ct,
        Action<FakeFrameTransport, SandboxFrame>? onOtherFrame = null, string? remoteIp = null)
    {
        var transport = new FakeFrameTransport();
        transport.Push(new HostHello(Guid.NewGuid(), hostId, "24.0.5", 1));
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
                default:
                    onOtherFrame?.Invoke(transport, frame);
                    break;
            }
            return Task.CompletedTask;
        };

        _ = registry.RunConnectionAsync(transport, ct, remoteIp);
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
    public async Task IsConnected_FalseUntilHandshakeCompletes_ThenTrue()
    {
        var (registry, _) = CreateRegistry();
        registry.IsConnected.Should().BeFalse();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ConnectBroker(registry, "host-1", cts.Token);

        await WaitUntilAsync(() => registry.IsConnected);
        registry.HostId.Should().Be("host-1");

        cts.Cancel();
    }

    [Fact]
    public void RemoteIp_NullBeforeAnyBrokerConnects()
    {
        var (registry, _) = CreateRegistry();

        registry.RemoteIp.Should().BeNull();
    }

    [Fact]
    public async Task RemoteIp_ReflectsTheConnectedBrokersAddress()
    {
        // The controller passes this through from HttpContext.Connection.RemoteIpAddress — the registry
        // itself never touches a socket's address directly, it just stores what it's handed at handshake
        // time. SandboxTool's "ports" action reads this back to build a real http://host:port URL.
        var (registry, _) = CreateRegistry();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ConnectBroker(registry, "host-1", cts.Token, remoteIp: "203.0.113.7");

        await WaitUntilAsync(() => registry.IsConnected);
        registry.RemoteIp.Should().Be("203.0.113.7");

        cts.Cancel();
    }

    [Fact]
    public async Task RemoteIp_ClearedAfterTheBrokerDisconnects()
    {
        var (registry, _) = CreateRegistry(new SandboxSettings
        {
            HeartbeatIntervalSeconds = 1,
            HeartbeatTimeoutSeconds = 1,
            RequestTimeoutSeconds = 5,
            ImagePullTimeoutSeconds = 5
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var transport = ConnectBroker(registry, "host-1", cts.Token, remoteIp: "203.0.113.7");
        await WaitUntilAsync(() => registry.IsConnected);
        registry.RemoteIp.Should().Be("203.0.113.7");

        transport.Complete(); // simulate the broker dropping off

        await WaitUntilAsync(() => !registry.IsConnected);
        registry.RemoteIp.Should().BeNull();
    }

    [Fact]
    public async Task RemoteIp_UpdatesWhenADifferentBrokerSupersedesTheConnection()
    {
        var (registry, _) = CreateRegistry();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ConnectBroker(registry, "host-A", cts.Token, remoteIp: "203.0.113.7");
        await WaitUntilAsync(() => registry.HostId == "host-A");

        ConnectBroker(registry, "host-B", cts.Token, remoteIp: "198.51.100.20");
        await WaitUntilAsync(() => registry.HostId == "host-B");

        registry.RemoteIp.Should().Be("198.51.100.20");

        cts.Cancel();
    }

    [Fact]
    public async Task IsConnected_GoesFalseAfterBrokerGoesSilent()
    {
        var (registry, _) = CreateRegistry(new SandboxSettings
        {
            HeartbeatIntervalSeconds = 1,
            HeartbeatTimeoutSeconds = 1,
            RequestTimeoutSeconds = 5,
            ImagePullTimeoutSeconds = 5
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // No Heartbeat echo configured beyond ConnectBroker's default — but the transport dies before any
        // heartbeat exchange, which is the scenario under test.
        var transport = new FakeFrameTransport();
        transport.Push(new HostHello(Guid.NewGuid(), "host-1", "24.0.5", 1));
        transport.OnSend = frame =>
        {
            if (frame is ListSandboxesRequest lsr)
                transport.Push(new ListSandboxesResponse(lsr.RequestId, []));
            // Heartbeats are deliberately left unanswered from here on.
            return Task.CompletedTask;
        };
        _ = registry.RunConnectionAsync(transport, cts.Token);

        await WaitUntilAsync(() => registry.IsConnected);

        transport.Complete(); // simulate the broker dropping off mid-connection

        await WaitUntilAsync(() => !registry.IsConnected);
    }

    [Fact]
    public async Task DuplicateConnection_NewestWins()
    {
        var (registry, _) = CreateRegistry();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ConnectBroker(registry, "host-A", cts.Token);
        await WaitUntilAsync(() => registry.HostId == "host-A");

        ConnectBroker(registry, "host-B", cts.Token);
        await WaitUntilAsync(() => registry.HostId == "host-B");

        registry.IsConnected.Should().BeTrue();
        registry.HostId.Should().Be("host-B");

        cts.Cancel();
    }

    [Fact]
    public async Task ExecAsync_NoBrokerConnected_ThrowsSandboxHostDisconnectedException()
    {
        var (registry, _) = CreateRegistry();

        var act = async () => await registry.ExecAsync(Guid.NewGuid(), ["true"], null, TimeSpan.FromSeconds(5), CancellationToken.None);

        await act.Should().ThrowAsync<SandboxHostDisconnectedException>();
    }

    [Fact]
    public async Task DestroySandboxAsync_NoBrokerConnected_ThrowsSandboxHostDisconnectedException()
    {
        var (registry, _) = CreateRegistry();

        var act = async () => await registry.DestroySandboxAsync(Guid.NewGuid(), true, CancellationToken.None);

        await act.Should().ThrowAsync<SandboxHostDisconnectedException>();
    }

    [Fact]
    public async Task ExecAsync_AccumulatesChunksPerStream()
    {
        var (registry, _) = CreateRegistry();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ConnectBroker(registry, "host-1", cts.Token, (transport, frame) =>
        {
            if (frame is not ExecRequest exec) return;
            transport.Push(new ExecOutputChunk(exec.RequestId, StreamKind.Stdout, 1, Convert.ToBase64String("hello "u8.ToArray())));
            transport.Push(new ExecOutputChunk(exec.RequestId, StreamKind.Stdout, 2, Convert.ToBase64String("world"u8.ToArray())));
            transport.Push(new ExecOutputChunk(exec.RequestId, StreamKind.Stderr, 1, Convert.ToBase64String("warn"u8.ToArray())));
            transport.Push(new ExecCompleted(exec.RequestId, 0, false, null));
        });
        await WaitUntilAsync(() => registry.IsConnected);

        var result = await registry.ExecAsync(Guid.NewGuid(), ["echo", "hi"], null, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.TimedOut.Should().BeFalse();
        result.Stdout.Should().Be("hello world");
        result.Stderr.Should().Be("warn");
        result.StdoutTruncated.Should().BeFalse();
        result.StderrTruncated.Should().BeFalse();

        cts.Cancel();
    }

    [Fact]
    public async Task ExecAsync_RespectsMaxOutputCharsPerStream_KeepingTheEndOfTheOutput()
    {
        var (registry, _) = CreateRegistry(new SandboxSettings
        {
            HeartbeatIntervalSeconds = 1,
            HeartbeatTimeoutSeconds = 1,
            RequestTimeoutSeconds = 5,
            ImagePullTimeoutSeconds = 5,
            MaxOutputCharsPerStream = 9
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ConnectBroker(registry, "host-1", cts.Token, (transport, frame) =>
        {
            if (frame is not ExecRequest exec) return;
            transport.Push(new ExecOutputChunk(exec.RequestId, StreamKind.Stdout, 1, Convert.ToBase64String("Hello"u8.ToArray())));
            transport.Push(new ExecOutputChunk(exec.RequestId, StreamKind.Stdout, 2, Convert.ToBase64String(", World"u8.ToArray())));
            transport.Push(new ExecOutputChunk(exec.RequestId, StreamKind.Stderr, 1, Convert.ToBase64String("boom"u8.ToArray())));
            transport.Push(new ExecCompleted(exec.RequestId, 1, false, null));
        });
        await WaitUntilAsync(() => registry.IsConnected);

        var result = await registry.ExecAsync(Guid.NewGuid(), ["python3", "bad.py"], null, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.ExitCode.Should().Be(1);
        // "Hello" + ", World" is 12 chars against a 9-char budget, so something must go. What survives
        // must include the END of the stream, not just the start: for shell output the error message is
        // the last thing printed, so head-only truncation would discard exactly the part worth reading.
        result.Stdout.Should().StartWith("Hel");
        result.Stdout.Should().EndWith(" World");
        result.Stdout.Should().Contain("elided");
        result.StdoutTruncated.Should().BeTrue();
        result.Stderr.Should().Be("boom");
        result.StderrTruncated.Should().BeFalse();

        cts.Cancel();
    }
}
