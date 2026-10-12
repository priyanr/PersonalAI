using FluentAssertions;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.Tests.Sandbox;

public class SandboxFramePumpTests
{
    [Fact]
    public async Task SendRequestAsync_CorrelatesResponseByRequestId()
    {
        var transport = new FakeFrameTransport();
        transport.OnSend = frame =>
        {
            if (frame is HostHello hello)
                transport.Push(new HostHelloAck(hello.RequestId, true, null));
            return Task.CompletedTask;
        };

        await using var pump = new SandboxFramePump(transport);

        var request = new HostHello(Guid.NewGuid(), "host-1", "24.0.5", 1);
        var response = await pump.SendRequestAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);

        response.Should().BeOfType<HostHelloAck>();
        response.RequestId.Should().Be(request.RequestId);
    }

    [Fact]
    public async Task RegisterChunkHandler_DeliversChunksInOrderThenTerminalCompletion()
    {
        var transport = new FakeFrameTransport();
        var requestId = Guid.NewGuid();

        transport.OnSend = _ =>
        {
            transport.Push(new ExecOutputChunk(requestId, StreamKind.Stdout, 1, Convert.ToBase64String("a"u8.ToArray())));
            transport.Push(new ExecOutputChunk(requestId, StreamKind.Stdout, 2, Convert.ToBase64String("b"u8.ToArray())));
            transport.Push(new ExecCompleted(requestId, 0, false, null));
            return Task.CompletedTask;
        };

        await using var pump = new SandboxFramePump(transport);

        var received = new List<SandboxFrame>();
        pump.RegisterChunkHandler(requestId, received.Add);

        var request = new ExecRequest(requestId, Guid.NewGuid(), ["echo", "hi"], null, 30);
        var result = await pump.SendRequestAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeOfType<ExecCompleted>();
        received.Should().HaveCount(3);
        received[0].Should().BeOfType<ExecOutputChunk>().Which.Sequence.Should().Be(1);
        received[1].Should().BeOfType<ExecOutputChunk>().Which.Sequence.Should().Be(2);
        received[2].Should().BeOfType<ExecCompleted>();
    }

    [Fact]
    public async Task SendRequestAsync_ChunksWithNoHandlerRegistered_StillResolvesOnTerminalCompletion()
    {
        // Regression guard: an ExecOutputChunk is never a response. Chunks share the ExecRequest's
        // RequestId, so if dispatch only skipped them while a chunk handler happened to be registered,
        // a chunk arriving first here would resolve the await with the wrong frame type.
        var transport = new FakeFrameTransport();
        var requestId = Guid.NewGuid();

        transport.OnSend = _ =>
        {
            transport.Push(new ExecOutputChunk(requestId, StreamKind.Stdout, 1, Convert.ToBase64String("a"u8.ToArray())));
            transport.Push(new ExecCompleted(requestId, 7, false, null));
            return Task.CompletedTask;
        };

        await using var pump = new SandboxFramePump(transport);

        var request = new ExecRequest(requestId, Guid.NewGuid(), ["echo", "hi"], null, 30);
        var result = await pump.SendRequestAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Should().BeOfType<ExecCompleted>().Which.ExitCode.Should().Be(7);
    }

    [Fact]
    public async Task SendRequestAsync_NoResponseWithinTimeout_ThrowsTimeoutException()
    {
        var transport = new FakeFrameTransport(); // OnSend unset — nothing ever answers

        await using var pump = new SandboxFramePump(transport);

        var request = new Heartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var act = async () => await pump.SendRequestAsync(request, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task TransportDeath_FaultsEveryPendingRequest()
    {
        var transport = new FakeFrameTransport(); // OnSend unset — requests never get a response

        await using var pump = new SandboxFramePump(transport);

        var task1 = pump.SendRequestAsync(new Heartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow), TimeSpan.FromSeconds(30), CancellationToken.None);
        var task2 = pump.SendRequestAsync(new Heartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow), TimeSpan.FromSeconds(30), CancellationToken.None);

        transport.Complete(); // simulate the transport dying

        var act1 = async () => await task1;
        var act2 = async () => await task2;
        await act1.Should().ThrowAsync<SandboxHostDisconnectedException>();
        await act2.Should().ThrowAsync<SandboxHostDisconnectedException>();
    }

    [Fact]
    public async Task Completion_ResolvesWhenTransportClosesWithNothingPending()
    {
        // Regression guard for the broker never reconnecting: with nothing in flight the writer loop sits
        // on an empty outbox, so the reader loop exiting has to shut it down or Completion (which
        // BrokerConnection awaits before reconnecting) never resolves.
        var transport = new FakeFrameTransport();

        await using var pump = new SandboxFramePump(transport);

        transport.Complete();

        var act = () => pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Completion_ResolvesWhenReceiveThrows()
    {
        // The KeepAliveTimeout abort path: ReceiveAsync faults rather than returning a graceful close.
        await using var pump = new SandboxFramePump(new ThrowingReceiveTransport());

        var act = () => pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendAsync_AfterTransportClosed_ThrowsDisconnected()
    {
        var transport = new FakeFrameTransport();

        await using var pump = new SandboxFramePump(transport);

        transport.Complete();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var act = async () => await pump.SendAsync(new Heartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow), CancellationToken.None);
        await act.Should().ThrowAsync<SandboxHostDisconnectedException>();
    }

    private sealed class ThrowingReceiveTransport : IFrameTransport
    {
        public Task SendAsync(SandboxFrame frame, CancellationToken ct) => Task.CompletedTask;

        public Task<SandboxFrame?> ReceiveAsync(CancellationToken ct) =>
            Task.FromException<SandboxFrame?>(new System.Net.WebSockets.WebSocketException("The remote party closed the connection."));
    }

    [Fact]
    public async Task ConcurrentSendRequests_NeverOverlapOnTheWire()
    {
        var transport = new FakeFrameTransport();
        var concurrentSends = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();

        transport.OnSend = async frame =>
        {
            lock (gate)
            {
                concurrentSends++;
                maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentSends);
            }

            // Long enough that two overlapping sends would reliably both be "in flight" at once and get
            // caught by the counter above, if the writer loop ever allowed that.
            await Task.Delay(20);

            lock (gate) concurrentSends--;

            transport.Push(new Heartbeat(frame.RequestId, DateTimeOffset.UtcNow));
        };

        await using var pump = new SandboxFramePump(transport);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => pump.SendRequestAsync(new Heartbeat(Guid.NewGuid(), DateTimeOffset.UtcNow), TimeSpan.FromSeconds(5), CancellationToken.None))
            .ToList();

        await Task.WhenAll(tasks);

        maxObservedConcurrency.Should().Be(1);
    }
}
