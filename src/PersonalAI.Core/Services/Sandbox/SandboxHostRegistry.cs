using System.Net.WebSockets;
using System.Text;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Sandbox.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.Sandbox;

/// <summary>Owns the one WebSocket connection the sandbox host dials in with (the API runs as a single replica so this
/// can be a singleton) and implements ISandboxHostClient over it. The hello handshake is done on the raw transport
/// before the request/response pump starts, because the host's opening frame isn't an answer to a request.</summary>
public sealed class SandboxHostRegistry(
    IOptions<SandboxSettings> settings,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SandboxHostRegistry> logger) : ISandboxHostClient
{
    private sealed class Connection
    {
        public required SandboxFramePump Pump { get; init; }
        public required WebSocket? Socket { get; init; }
        public required string HostId { get; init; }
        public required DateTimeOffset ConnectedAt { get; init; }
        public string? RemoteIp { get; init; }
        private int _disposed;

        /// <summary>Both the superseding connection and this connection's own cleanup race to dispose it
        /// — guard so SandboxFramePump.DisposeAsync (which disposes its internal CancellationTokenSource)
        /// never runs twice.</summary>
        public async Task DisposeOnceAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            await Pump.DisposeAsync();
        }
    }

    private readonly object _gate = new();
    private Connection? _connection;

    private Connection? Current { get { lock (_gate) return _connection; } }

    public bool IsConnected => Current is not null;
    public string? HostId => Current?.HostId;
    public DateTimeOffset? ConnectedAt => Current?.ConnectedAt;
    public string? RemoteIp => Current?.RemoteIp;

    /// <summary>Entry point for SandboxHostController: accepts the just-upgraded socket and blocks for the
    /// connection's entire lifetime. The controller action must await this call in full — returning early
    /// tears the accepted WebSocket down (an ASP.NET Core rule for WebSocket actions), and it's the only
    /// thing keeping the broker's outbound dial alive. <paramref name="remoteIp"/> is captured by the
    /// controller from HttpContext.Connection.RemoteIpAddress (or a trusted X-Forwarded-For, already
    /// resolved by Program.cs's UseForwardedHeaders) at accept time — the registry has no HttpContext of
    /// its own to read it from later.</summary>
    public Task HandleConnectionAsync(WebSocket socket, string? remoteIp, CancellationToken ct)
    {
        return RunConnectionAsync(new WebSocketFrameTransport(socket, MaxFrameBytesFor(settings.Value)), socket, remoteIp, ct);
    }

    /// <summary>Thin adapter over SandboxFrameLimits.MaxFrameBytesFor — kept as its own overload so callers
    /// (and MaxFrameBytesTests) can pass a SandboxSettings directly rather than pulling its two fields out
    /// by hand. See SandboxFrameLimits' own doc comment for why the actual formula lives there and not
    /// here or in BrokerConnection's identically-shaped overload.</summary>
    public static int MaxFrameBytesFor(SandboxSettings settings)
    {
        return SandboxFrameLimits.MaxFrameBytesFor(settings.MaxZipBytes, settings.MaxDownloadBytes);
    }

    /// <summary>Test seam: drives the identical handshake/reconcile/heartbeat lifecycle over a fake
    /// transport, with no real socket. Internal because production code only ever has a WebSocket to
    /// offer — see SandboxHostRegistryTests. <paramref name="remoteIp"/> defaults to null so every
    /// existing two-argument call site keeps compiling unchanged.</summary>
    internal Task RunConnectionAsync(IFrameTransport transport, CancellationToken ct, string? remoteIp = null)
    {
        return RunConnectionAsync(transport, socket: null, remoteIp, ct);
    }

    private async Task RunConnectionAsync(IFrameTransport transport, WebSocket? socket, string? remoteIp, CancellationToken ct)
    {
        SandboxFrame? first;
        try
        {
            first = await transport.ReceiveAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sandbox host connection failed before completing the handshake.");
            return;
        }

        if (first is not HostHello hello)
        {
            logger.LogWarning("Sandbox host connection opened with {FrameType} instead of HostHello; rejecting.",
                first?.GetType().Name ?? "no frame");
            return;
        }

        await transport.SendAsync(new HostHelloAck(hello.RequestId, true, null), ct);

        var pump = new SandboxFramePump(transport);
        var connection = new Connection
        {
            Pump = pump,
            Socket = socket,
            HostId = hello.HostId,
            ConnectedAt = timeProvider.GetUtcNow(),
            RemoteIp = remoteIp
        };

        Connection? previous;
        lock (_gate)
        {
            previous = _connection;
            _connection = connection;
        }

        if (previous is not null)
        {
            logger.LogInformation("Sandbox host {NewHostId} superseding existing connection {OldHostId} — newest wins.",
                connection.HostId, previous.HostId);
            await SupersedeAsync(previous, ct);
        }

        try
        {
            await ReconcileAsync(pump, ct);
            await RunHeartbeatLoopAsync(pump, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — the API is stopping or the request was aborted.
        }
        catch (SandboxHostDisconnectedException ex)
        {
            logger.LogInformation(ex, "Sandbox host {HostId} disconnected.", connection.HostId);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Sandbox host {HostId} missed a heartbeat within {Timeout}s; closing the connection.",
                connection.HostId, settings.Value.HeartbeatTimeoutSeconds);
            await TryAbortAsync(socket);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                    _connection = null;
            }
            await connection.DisposeOnceAsync();
        }
    }

    private async Task SupersedeAsync(Connection previous, CancellationToken ct)
    {
        try
        {
            if (previous.Socket is { State: WebSocketState.Open } socket)
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "superseded", ct);
        }
        catch (Exception ex)
        {
            // Best-effort: the old socket may already be half-dead, which is exactly the scenario this
            // exists to handle (a restarted broker leaving a stale connection the API still thinks is live).
            logger.LogDebug(ex, "Closing the superseded sandbox host connection {HostId} failed.", previous.HostId);
        }

        await previous.DisposeOnceAsync();
    }

    private static Task TryAbortAsync(WebSocket? socket)
    {
        try
        {
            socket?.Abort();
        }
        catch (Exception)
        {
            // Best-effort — the socket may already be gone.
        }
        return Task.CompletedTask;
    }

    private async Task RunHeartbeatLoopAsync(SandboxFramePump pump, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.HeartbeatIntervalSeconds));
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.Value.HeartbeatTimeoutSeconds));

        while (true)
        {
            await Task.Delay(interval, ct);
            // Reuses the pump's own request/timeout machinery as the liveness probe: the broker replies
            // with a Heartbeat echoing the same RequestId, which resolves this exactly like any other
            // correlated request. A TimeoutException here IS "no frame within HeartbeatTimeoutSeconds".
            await pump.SendRequestAsync(new Heartbeat(Guid.NewGuid(), timeProvider.GetUtcNow()), timeout, ct);
        }
    }

    /// <summary>DB row + broker container list reconciliation. Runs in a fresh DI scope because
    /// this registry is a singleton but ISandboxRepository is Scoped (it wraps the Scoped AppDbContext) —
    /// same captive-dependency workaround GmailTool documents for its own scoped DB writes.</summary>
    private async Task ReconcileAsync(SandboxFramePump pump, CancellationToken ct)
    {
        var requestTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.Value.RequestTimeoutSeconds));

        SandboxFrame response;
        try
        {
            response = await pump.SendRequestAsync(new ListSandboxesRequest(Guid.NewGuid()), requestTimeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or SandboxHostDisconnectedException)
        {
            logger.LogWarning(ex, "Sandbox host did not answer ListSandboxesRequest during reconciliation; skipping adoption for this connection.");
            return;
        }

        if (response is not ListSandboxesResponse list)
        {
            logger.LogWarning("Unexpected response to ListSandboxesRequest: {Type}", response.GetType().Name);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISandboxRepository>();
        var dbRows = await repository.GetAllActiveAsync(ct);

        foreach (var result in SandboxReconciler.Reconcile(dbRows, list.Sandboxes))
        {
            switch (result.Transition)
            {
                case SandboxReconciler.Transition.MarkRunning:
                    result.Instance!.Status = SandboxInstanceStatus.Running;
                    result.Instance.ContainerId = result.ContainerId;
                    result.Instance.LastUsedAt = timeProvider.GetUtcNow();
                    break;

                case SandboxReconciler.Transition.MarkOrphaned:
                    result.Instance!.Status = SandboxInstanceStatus.Orphaned;
                    break;

                case SandboxReconciler.Transition.Untracked:
                    logger.LogWarning(
                        "Sandbox host reported untracked container {ContainerId} with no matching active SandboxInstance row; leaving it for the reaper's grace period.",
                        result.ContainerId);
                    break;
            }
        }
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(ct);
    }

    public async Task<SandboxEnsureResult> EnsureSandboxAsync(Guid sandboxId, SandboxKind kind, Guid ownerId, string image, IReadOnlyList<int> preferredPorts, CancellationToken ct)
    {
        var connection = RequireConnection();
        var config = settings.Value;
        var request = new CreateSandboxRequest(
            Guid.NewGuid(), sandboxId, kind, ownerId, image,
            config.CpuLimitMilliCores, config.MemoryLimitBytes, config.PidsLimit,
            config.RunAsNonRoot, config.CapDrop, preferredPorts);

        // First use of a sandbox may pull an image, which can dwarf every other request's timeout.
        var timeout = TimeSpan.FromSeconds(Math.Max(1, config.ImagePullTimeoutSeconds));
        var response = await connection.Pump.SendRequestAsync(request, timeout, ct);

        if (response is not CreateSandboxResponse created)
            throw new InvalidOperationException($"Unexpected response to CreateSandboxRequest: {response.GetType().Name}");
        if (!created.Success || created.HostPath is null)
            throw new InvalidOperationException(created.Error ?? "The sandbox host failed to create the sandbox.");

        return new SandboxEnsureResult(created.HostPath, created.Ports);
    }

    public async Task<SandboxExecResult> ExecAsync(Guid sandboxId, IReadOnlyList<string> command, string? workingDir, TimeSpan timeout, CancellationToken ct)
    {
        var connection = RequireConnection();
        var requestId = Guid.NewGuid();
        var maxChars = Math.Max(0, settings.Value.MaxOutputCharsPerStream);

        var stdout = new BoundedOutputAccumulator(maxChars);
        var stderr = new BoundedOutputAccumulator(maxChars);

        void AppendChunk(StreamKind stream, string dataBase64)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(dataBase64); }
            catch (FormatException) { return; } // malformed chunk — drop rather than fault the whole exec

            (stream == StreamKind.Stdout ? stdout : stderr).Append(bytes);
        }

        connection.Pump.RegisterChunkHandler(requestId, frame =>
        {
            if (frame is ExecOutputChunk chunk)
                AppendChunk(chunk.Stream, chunk.DataBase64);
        });

        var seconds = (int)Math.Clamp(timeout.TotalSeconds, 1, int.MaxValue);
        var request = new ExecRequest(requestId, sandboxId, command, workingDir, seconds);
        // Give the pump-level wait some headroom over the exec's own timeout so the broker has a chance
        // to notice the timeout itself and report ExecCompleted(TimedOut: true) rather than us timing out
        // the wait first and losing that detail.
        var pumpTimeout = timeout + TimeSpan.FromSeconds(30);
        var response = await connection.Pump.SendRequestAsync(request, pumpTimeout, ct);

        if (response is not ExecCompleted completed)
            throw new InvalidOperationException($"Unexpected response to ExecRequest: {response.GetType().Name}");

        return new SandboxExecResult(completed.ExitCode, completed.TimedOut,
            stdout.ToString(), stderr.ToString(), stdout.Truncated, stderr.Truncated,
            completed.Error, completed.ContainerMissing);
    }

    public async Task<FileOpResponse> FileOpAsync(FileOpRequest request, CancellationToken ct)
    {
        var connection = RequireConnection();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.Value.RequestTimeoutSeconds));
        var response = await connection.Pump.SendRequestAsync(request, timeout, ct);

        if (response is not FileOpResponse fileOpResponse)
            throw new InvalidOperationException($"Unexpected response to FileOpRequest: {response.GetType().Name}");

        return fileOpResponse;
    }

    public async Task DestroySandboxAsync(Guid sandboxId, bool deleteFiles, CancellationToken ct)
    {
        var connection = RequireConnection();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.Value.RequestTimeoutSeconds));
        var response = await connection.Pump.SendRequestAsync(new DestroySandboxRequest(Guid.NewGuid(), sandboxId, deleteFiles), timeout, ct);

        if (response is DestroySandboxResponse { Success: false } failed)
            throw new InvalidOperationException(failed.Error ?? "The sandbox host failed to destroy the sandbox.");
    }

    private Connection RequireConnection()
    {
        return Current ?? throw new SandboxHostDisconnectedException("No sandbox host is connected.");
    }
}
