using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.SandboxHost;

/// <summary>Owns the one outbound WebSocket to the API: dials out (never accepts inbound), authenticates
/// with a header (never a query string — see BrokerSettings.HostKey), performs the HostHello/HostHelloAck
/// handshake, then sits reacting to inbound request frames via SandboxFramePump.OnRequest until the
/// connection dies, at which point it reconnects with jittered exponential backoff. This is deliberately
/// the *only* place in the broker that owns a ClientWebSocket or a SandboxFramePump instance — everything
/// else (DockerSandboxManager, SandboxFileService) is pure request-in/response-out and knows nothing about
/// the transport.</summary>
public sealed class BrokerConnection(
    IOptions<BrokerSettings> settings,
    DockerSandboxManager dockerManager,
    SandboxFileService fileService,
    ILogger<BrokerConnection> logger) : BackgroundService
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly BrokerSettings _settings = settings.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = MinBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            var harderBackoff = false;

            try
            {
                await RunOnceAsync(stoppingToken);
                // A clean run (connected, served requests, then the socket eventually dropped) is not a
                // config error — reset so the next reconnect attempt starts fast again.
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // normal shutdown
            }
            catch (SandboxHelloRejectedException ex)
            {
                logger.LogError(ex, "Sandbox host key rejected by the API — check Broker:HostKey against Sandbox:HostKey.");
                harderBackoff = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sandbox broker connection to {ApiUrl} dropped; reconnecting.", _settings.ApiUrl);
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            await DelayWithJitterAsync(backoff, stoppingToken);
            backoff = NextBackoff(backoff, harderBackoff);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Sandbox-Host-Key", _settings.HostKey);
        // Without an enforced KeepAliveTimeout, a connection that dies silently on the wire (sleep/resume,
        // a network change, an idle NAT mapping expiring) never gets an exception out of ReceiveAsync, so
        // ExecuteAsync's reconnect loop never regains control — see BrokerSettings.KeepAliveTimeoutSeconds.
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(_settings.KeepAliveTimeoutSeconds);
        // Bounded like the HostHelloAck receive below: a half-open connect or a hung Docker socket must
        // not stall the reconnect loop any more than a dead established connection may.
        var handshakeTimeout = TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds);
        await socket.ConnectAsync(new Uri(_settings.ApiUrl), ct).WaitAsync(handshakeTimeout, ct);

        var transport = new WebSocketFrameTransport(socket, MaxFrameBytesFor(_settings));
        var dockerVersion = await dockerManager.GetDockerVersionAsync(ct).WaitAsync(handshakeTimeout, ct);

        // The HostHello/HostHelloAck handshake happens directly over the raw transport, before a
        // SandboxFramePump exists on either side — mirrors SandboxHostRegistry.RunConnectionAsync on the
        // API side exactly. The pump's correlation model is "this side sends a request, the other side
        // echoes the RequestId back", which fits every frame the API subsequently initiates (ListSandboxes,
        // CreateSandbox, Exec, FileOp, Destroy, Heartbeat) but not this unprompted opening frame.
        var helloRequestId = Guid.NewGuid();
        await transport.SendAsync(new HostHello(helloRequestId, _settings.HostId, dockerVersion, ProtocolVersion), ct);

        var ackFrame = await transport.ReceiveAsync(ct)
            .WaitAsync(handshakeTimeout, ct);

        if (ackFrame is not HostHelloAck { Accepted: true } ack || ack.RequestId != helloRequestId)
        {
            var reason = (ackFrame as HostHelloAck)?.Reason;
            throw new SandboxHelloRejectedException(reason);
        }

        await using var pump = new SandboxFramePump(transport);
        pump.OnRequest = frame => DispatchFireAndForget(pump, frame, ct);

        logger.LogInformation(
            "Sandbox broker connected to {ApiUrl} as host {HostId} (Docker {DockerVersion}).",
            _settings.ApiUrl, _settings.HostId, dockerVersion);

        // Nothing else here is waiting on anything — in steady state the broker only reacts to
        // OnRequest. Completion resolves once the reader loop observes the socket die (gracefully or not —
        // KeepAliveTimeout above turns a silent drop into a receive failure) and shuts the idle writer
        // down with it; both loops swallow their own exceptions, so this itself never throws. That is this
        // method's cue to fall through, dispose the pump/socket, and let ExecuteAsync reconnect.
        await pump.Completion;
        logger.LogInformation("Sandbox broker connection to {ApiUrl} closed.", _settings.ApiUrl);
    }

    /// <summary>Thin adapter over SandboxFrameLimits.MaxFrameBytesFor, mirroring
    /// SandboxHostRegistry.MaxFrameBytesFor's identically-shaped overload on the API side — see
    /// SandboxFrameLimits' own doc comment for why the actual formula lives in the shared protocol
    /// project rather than being duplicated in each of these two.</summary>
    public static int MaxFrameBytesFor(BrokerSettings settings) =>
        SandboxFrameLimits.MaxFrameBytesFor(settings.MaxZipBytes, settings.MaxDownloadBytes);

    /// <summary>SandboxFramePump.OnRequest is a synchronous callback invoked from the reader loop — it
    /// must return immediately so the loop can keep reading (a second request arriving while the first is
    /// still executing must not block behind it). Actual handling runs on the thread pool; any exception
    /// that escapes HandleFrameAsync's own try/catch would otherwise be silently lost on a fire-and-forget
    /// task, so it is caught here too as a last resort and logged.</summary>
    private void DispatchFireAndForget(SandboxFramePump pump, SandboxFrame frame, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleFrameAsync(pump, frame, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception dispatching {FrameType}", frame.GetType().Name);
            }
        }, ct);
    }

    private async Task HandleFrameAsync(SandboxFramePump pump, SandboxFrame frame, CancellationToken ct)
    {
        try
        {
            switch (frame)
            {
                case Heartbeat heartbeat:
                    // The API implements liveness as pump.SendRequestAsync(Heartbeat, HeartbeatTimeout) —
                    // a real correlated request, not a passive "last frame seen" clock. It must get back a
                    // Heartbeat echoing the SAME RequestId or it times out and tears the connection down
                    // (see SandboxHostRegistry.RunHeartbeatLoopAsync). Fire-and-forget SendAsync, not
                    // SendRequestAsync — we are replying to their request, not issuing one of our own.
                    await pump.SendAsync(new Heartbeat(heartbeat.RequestId, DateTimeOffset.UtcNow), ct);
                    break;

                case ListSandboxesRequest listRequest:
                    await pump.SendAsync(await dockerManager.ListAsync(listRequest, ct), ct);
                    break;

                case CreateSandboxRequest createRequest:
                    await pump.SendAsync(await dockerManager.CreateAsync(createRequest, ct), ct);
                    break;

                case ExecRequest execRequest:
                    var completed = await dockerManager.ExecAsync(
                        execRequest,
                        (chunk, chunkCt) => pump.SendAsync(chunk, chunkCt),
                        ct);
                    await pump.SendAsync(completed, ct);
                    break;

                case FileOpRequest fileRequest:
                    await pump.SendAsync(await HandleFileOpAsync(fileRequest, ct), ct);
                    break;

                case DestroySandboxRequest destroyRequest:
                    await pump.SendAsync(await dockerManager.DestroyAsync(destroyRequest, ct), ct);
                    break;

                default:
                    logger.LogWarning("Received a frame type the broker never expects to receive: {FrameType}", frame.GetType().Name);
                    await pump.SendAsync(new ErrorFrame(frame.RequestId, $"Broker cannot handle frame type {frame.GetType().Name}."), ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling {FrameType} (RequestId {RequestId})", frame.GetType().Name, frame.RequestId);
            try
            {
                await pump.SendAsync(new ErrorFrame(frame.RequestId, ex.Message), CancellationToken.None);
            }
            catch
            {
                // Pump/transport is already dead — nothing left to notify.
            }
        }
    }

    private async Task<FileOpResponse> HandleFileOpAsync(FileOpRequest request, CancellationToken ct)
    {
        var ownerId = await dockerManager.ResolveOwnerIdAsync(request.SandboxId, ct);
        if (ownerId is null)
            return new FileOpResponse(request.RequestId, false,
                $"Unknown sandbox '{request.SandboxId}'; create it first.", ContainerMissing: true);

        return await fileService.ExecuteAsync(ownerId.Value, request, ct);
    }

    private static TimeSpan NextBackoff(TimeSpan current, bool harder)
    {
        var factor = harder ? 4 : 2;
        var next = TimeSpan.FromSeconds(Math.Min(current.TotalSeconds * factor, MaxBackoff.TotalSeconds));
        return next < MinBackoff ? MinBackoff : next;
    }

    private static Task DelayWithJitterAsync(TimeSpan backoff, CancellationToken ct)
    {
        // Full jitter: a uniformly random delay between 0 and the current backoff ceiling. Spreads out
        // reconnect storms if the API (or the network path to it) is what dropped, without ever waiting
        // longer than the ceiling itself.
        var jittered = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * backoff.TotalMilliseconds);
        return Task.Delay(jittered, ct);
    }
}
