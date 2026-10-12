using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.SandboxHost;

/// <summary>Everything that touches the Docker Engine API: create/exec/destroy/list. File operations do
/// NOT go through here — see SandboxFileService — because the whole point of the host-bind-mount design
/// is that file I/O is plain .NET filesystem access, not something that needs a container running at all.
///
/// Uses Docker.DotNet.Enhanced 4.3.3, not the unmaintained Docker.DotNet 3.x — the API differs in exactly
/// the ways documented on each call below (verified against a compiled reference before this was written).</summary>
public sealed class DockerSandboxManager : IAsyncDisposable
{
    private const string LabelSandbox = "personalai.sandbox";
    private const string LabelId = "personalai.sandbox.id";
    private const string LabelKind = "personalai.sandbox.kind";
    private const string LabelOwner = "personalai.sandbox.owner";
    private const string LabelCreatedAt = "personalai.sandbox.createdAtUtc";
    private const string LabelPorts = "personalai.sandbox.ports";

    /// <summary>How many times CreateAsync tries fresh ports after a host port conflict before giving up.</summary>
    private const int MaxCreateAttempts = 3;

    private readonly BrokerSettings _settings;
    private readonly ILogger<DockerSandboxManager> _logger;
    private readonly DockerClient _client;
    private readonly SandboxPortAllocator _portAllocator;

    /// <summary>SandboxId -> what we need to act on it again without re-deriving from Docker every time.
    /// Repopulated lazily from container labels (see ResolveAsync) whenever an entry is missing, which
    /// covers a broker restart: the DB/API always calls CreateSandboxRequest ("ensure") before any
    /// Exec/FileOp/Destroy, so in steady state this dictionary is warm, but nothing depends on that.</summary>
    private readonly ConcurrentDictionary<Guid, SandboxRecord> _sandboxes = new();

    public DockerSandboxManager(IOptions<BrokerSettings> settings, ILogger<DockerSandboxManager> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client = new DockerClientBuilder()
            .WithEndpoint(new Uri(_settings.DockerEndpoint))
            .WithTimeout(TimeSpan.FromSeconds(Math.Max(_settings.RequestTimeoutSeconds, _settings.ImagePullTimeoutSeconds)))
            .Build();
        _portAllocator = new SandboxPortAllocator(_settings.HostPortRangeStart, _settings.HostPortRangeEnd, _settings.ExposedPortCount);
    }

    public async Task<string> GetDockerVersionAsync(CancellationToken ct)
    {
        var version = await _client.System.GetVersionAsync(ct);
        return version.Version;
    }

    public async Task<CreateSandboxResponse> CreateAsync(CreateSandboxRequest request, CancellationToken ct)
    {
        try
        {
            var ownerDirectory = Path.Combine(_settings.SandboxRootPath, request.OwnerId.ToString());
            Directory.CreateDirectory(ownerDirectory);

            var containerName = ContainerName(request.SandboxId);

            // Ports proven bad by a bind failure during THIS create — kept out of every retry below, but not
            // reserved in the allocator, since whatever held them may well let go shortly.
            var rejectedPorts = new HashSet<int>();
            IReadOnlyList<int> preferredPorts = request.PreferredPorts;

            // Idempotent create: a restarted API (or a session simply being re-entered) may call
            // CreateSandboxRequest again for a sandbox whose container already exists. Adopt it instead
            // of failing on Docker's 409 name conflict — this is also what makes "docker rm the container,
            // then use the sandbox again" recreate cleanly rather than erroring on the second create that
            // never actually needed to happen.
            var existing = await FindContainerByNameAsync(containerName, ct);
            if (existing is not null)
            {
                // The container already has whatever ports it was created with baked into its actual port
                // bindings — recovered from its own label rather than re-allocated, and seeded into the
                // allocator so a second concurrent create for a DIFFERENT sandbox can't be handed the same
                // ports out from under this one.
                var existingPorts = ParsePortsLabel(existing.Labels);
                _portAllocator.Seed(existingPorts);

                try
                {
                    if (!string.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase))
                        await _client.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters(), ct);

                    _sandboxes[request.SandboxId] = new SandboxRecord(request.OwnerId, existing.ID, ownerDirectory, request.Kind, existingPorts);
                    return new CreateSandboxResponse(request.RequestId, true, existing.ID, ownerDirectory, existingPorts, null);
                }
                catch (DockerApiException ex) when (SandboxPortAllocator.IsPortConflict(ex.Message))
                {
                    // Port bindings are fixed at create time, so this container can never start again —
                    // retrying start would fail identically forever. Replace it: remove it and fall
                    // through to a fresh create that keeps every port except the one that collided.
                    // Files live in the owner's bind-mounted directory, so nothing is lost.
                    _logger.LogWarning(ex, "Sandbox {SandboxId} container cannot start because a published port is taken; recreating it on different ports.", request.SandboxId);
                    await RemoveContainerQuietlyAsync(existing.ID, ct);
                    _sandboxes.TryRemove(request.SandboxId, out _);
                    _portAllocator.Release(existingPorts);
                    RejectConflictPort(ex.Message, existingPorts, rejectedPorts, out preferredPorts);
                }
            }

            await EnsureImageAsync(request.Image, ct);

            // One snapshot per create rather than per probed port: host ports Docker already publishes for
            // ANY container (not just ours), plus every local TCP port the host is using right now.
            var busyPorts = await ListDockerPublishedPortsAsync(ct);
            busyPorts.UnionWith(SnapshotHostTcpPorts());

            for (var attempt = 1; ; attempt++)
            {
                var ports = _portAllocator.Allocate(
                    preferredPorts, p => !busyPorts.Contains(p) && !rejectedPorts.Contains(p) && IsPortFree(p));
                string? containerId = null;

                try
                {
                    var createParameters = BuildCreateParameters(request, containerName, ownerDirectory, ports);
                    var created = await _client.Containers.CreateContainerAsync(createParameters, ct);
                    containerId = created.ID;
                    await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

                    _sandboxes[request.SandboxId] = new SandboxRecord(request.OwnerId, created.ID, ownerDirectory, request.Kind, ports);
                    return new CreateSandboxResponse(request.RequestId, true, created.ID, ownerDirectory, ports, null);
                }
                catch (DockerApiException ex) when (attempt < MaxCreateAttempts && SandboxPortAllocator.IsPortConflict(ex.Message))
                {
                    // The live probe is only a point-in-time check, so something can still grab a port
                    // between it and Docker binding it. Drop that port and try again with the rest.
                    _logger.LogWarning(ex, "Sandbox {SandboxId} hit a host port conflict on attempt {Attempt}; retrying with different ports.", request.SandboxId, attempt);
                    await RemoveContainerQuietlyAsync(containerId, ct);
                    _portAllocator.Release(ports);
                    RejectConflictPort(ex.Message, ports, rejectedPorts, out preferredPorts);
                }
                catch
                {
                    // Docker creates the container before start binds its ports, so a failed start leaves
                    // a container behind. Remove it — otherwise the next ensure call would adopt it and
                    // retry the same doomed start — and give the ports back to the pool.
                    await RemoveContainerQuietlyAsync(containerId, ct);
                    _portAllocator.Release(ports);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sandbox {SandboxId}", request.SandboxId);
            return new CreateSandboxResponse(request.RequestId, false, null, null, [], ex.Message);
        }
    }

    private static CreateContainerParameters BuildCreateParameters(
        CreateSandboxRequest request, string containerName, string ownerDirectory, IReadOnlyList<int> ports)
    {
        var hostConfig = new HostConfig
        {
            Binds = [$"{ownerDirectory}:/work"],
            PortBindings = ports.ToDictionary(
                p => $"{p}/tcp",
                p => (IList<PortBinding>)[new PortBinding { HostPort = p.ToString() }]),
        };

        // Resource/security fields default to unlimited/permissive by design (see plan Context) — set
        // them only when the request actually carries a value, never a hardcoded default here.
        if (request.CpuLimitMilliCores is { } cpuMilliCores)
            hostConfig.NanoCPUs = cpuMilliCores * 1_000_000L;
        if (request.MemoryLimitBytes is { } memoryBytes)
            hostConfig.Memory = memoryBytes;
        if (request.PidsLimit is { } pidsLimit)
            hostConfig.PidsLimit = pidsLimit;
        if (request.CapDrop.Count > 0)
            hostConfig.CapDrop = request.CapDrop.ToList();

        var createParameters = new CreateContainerParameters
        {
            Image = request.Image,
            Name = containerName,
            Cmd = ["sleep", "infinity"],
            WorkingDir = "/work",
            ExposedPorts = ports.ToDictionary(p => $"{p}/tcp", _ => default(EmptyStruct)),
            Labels = new Dictionary<string, string>
            {
                [LabelSandbox] = "true",
                [LabelId] = request.SandboxId.ToString(),
                [LabelKind] = request.Kind.ToString().ToLowerInvariant(),
                [LabelOwner] = request.OwnerId.ToString(),
                [LabelCreatedAt] = DateTimeOffset.UtcNow.ToString("O"),
                [LabelPorts] = string.Join(",", ports),
            },
            HostConfig = hostConfig,
        };

        if (request.RunAsNonRoot)
            createParameters.User = "1000:1000";

        return createParameters;
    }

    /// <summary>Records which port a conflict error blamed so retries avoid it, and sets the next
    /// attempt's preferred ports to everything else from the failed set — the user keeps as many of the
    /// ports they may already depend on as possible. If the error doesn't name a port, every port in the
    /// failed set is suspect.</summary>
    private static void RejectConflictPort(
        string message, IReadOnlyList<int> attemptedPorts, HashSet<int> rejectedPorts, out IReadOnlyList<int> nextPreferred)
    {
        if (SandboxPortAllocator.ParseConflictPort(message) is { } badPort)
        {
            rejectedPorts.Add(badPort);
            nextPreferred = attemptedPorts.Where(p => p != badPort).ToList();
        }
        else
        {
            rejectedPorts.UnionWith(attemptedPorts);
            nextPreferred = [];
        }
    }

    private async Task RemoveContainerQuietlyAsync(string? containerId, CancellationToken ct)
    {
        if (containerId is null)
            return;

        try
        {
            await _client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (Exception ex)
        {
            // Best-effort cleanup on an already-failing path: surface the original error, not this one.
            _logger.LogWarning(ex, "Failed to remove container {ContainerId} after a failed sandbox create.", containerId);
        }
    }

    /// <summary>Host ports Docker currently publishes for every container on the daemon — including
    /// compose services, other apps' containers and other brokers' sandboxes — none of which this
    /// broker's allocator knows about otherwise.</summary>
    private async Task<HashSet<int>> ListDockerPublishedPortsAsync(CancellationToken ct)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
        return containers
            .SelectMany(c => c.Ports ?? [])
            .Where(p => p.PublicPort != 0)
            .Select(p => (int)p.PublicPort)
            .ToHashSet();
    }

    /// <summary>Every local TCP port in use on this machine right now — listeners AND the local end of
    /// established/TIME_WAIT connections. The bind probe alone misses the latter: an outbound connection
    /// takes its local port from the OS ephemeral range, which a bind test can't reliably see but which
    /// still makes Docker's own bind fail.</summary>
    private IEnumerable<int> SnapshotHostTcpPorts()
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            return properties.GetActiveTcpListeners().Select(e => e.Port)
                .Concat(properties.GetActiveTcpConnections().Select(c => c.LocalEndPoint.Port))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not snapshot host TCP ports; relying on the bind probe alone.");
            return [];
        }
    }

    public async Task<ExecCompleted> ExecAsync(
        ExecRequest request, Func<ExecOutputChunk, CancellationToken, Task> emitChunk, CancellationToken ct)
    {
        var record = await ResolveAsync(request.SandboxId, ct);
        if (record is null)
            return new ExecCompleted(request.RequestId, null, false,
                $"Unknown sandbox '{request.SandboxId}'; create it first.", ContainerMissing: true);

        try
        {
            var execCreateParameters = new ContainerExecCreateParameters
            {
                Cmd = request.Command.ToList(),
                AttachStdout = true,
                AttachStderr = true,
                TTY = false,
                WorkingDir = string.IsNullOrWhiteSpace(request.WorkingDir) ? "/work" : request.WorkingDir,
            };

            var exec = await _client.Exec.CreateContainerExecAsync(record.ContainerId, execCreateParameters, ct);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            using var stream = await _client.Exec.StartContainerExecAsync(
                exec.ID, new ContainerExecStartParameters { Detach = false, TTY = false }, linkedCts.Token);

            var sequence = 0L;
            long stdoutBytes = 0, stderrBytes = 0;
            var stdoutTruncated = false;
            var stderrTruncated = false;
            var buffer = new byte[_settings.ExecChunkBytes];
            var timedOut = false;

            try
            {
                while (true)
                {
                    var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, linkedCts.Token);
                    if (read.EOF)
                        break;
                    if (read.Count == 0)
                        continue;

                    var isStderr = read.Target == MultiplexedStream.TargetStream.StandardError;
                    ref long sentBytes = ref (isStderr ? ref stderrBytes : ref stdoutBytes);
                    ref bool truncatedFlag = ref (isStderr ? ref stderrTruncated : ref stdoutTruncated);

                    if (sentBytes >= _settings.MaxExecOutputBytesPerStream)
                    {
                        truncatedFlag = true;
                        continue; // keep draining so the process isn't blocked on a full pipe, just stop emitting
                    }

                    var take = (int)Math.Min(read.Count, _settings.MaxExecOutputBytesPerStream - sentBytes);
                    sentBytes += take;

                    var chunk = new ExecOutputChunk(
                        request.RequestId,
                        isStderr ? StreamKind.Stderr : StreamKind.Stdout,
                        Interlocked.Increment(ref sequence),
                        Convert.ToBase64String(buffer, 0, take));
                    await emitChunk(chunk, ct);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                timedOut = true;
            }

            if (timedOut)
                return new ExecCompleted(request.RequestId, null, true, "Command exceeded its timeout.");

            var inspect = await _client.Exec.InspectContainerExecAsync(exec.ID, ct);
            var exitCode = inspect.ExitCode is { } code ? (int)code : (int?)null;
            return new ExecCompleted(request.RequestId, exitCode, false, null);
        }
        catch (DockerApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            // 404: the container was removed out-of-band (docker rm). 409: it still exists but isn't
            // running (killed, OOM'd, host rebooted without the broker itself restarting). Either way the
            // exec never started, so evict the stale cache entry -- the next call re-resolves from Docker
            // instead of retrying against the same dead id -- and tell the caller the container itself is
            // gone so it can self-heal instead of surfacing a bare error.
            _sandboxes.TryRemove(request.SandboxId, out _);
            _logger.LogWarning(ex, "Exec target container for sandbox {SandboxId} is gone or not running; evicting cached record.", request.SandboxId);
            return new ExecCompleted(request.RequestId, null, false, ex.Message, ContainerMissing: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exec failed for sandbox {SandboxId}", request.SandboxId);
            return new ExecCompleted(request.RequestId, null, false, ex.Message);
        }
    }

    public async Task<DestroySandboxResponse> DestroyAsync(DestroySandboxRequest request, CancellationToken ct)
    {
        try
        {
            var record = await ResolveAsync(request.SandboxId, ct);
            if (record is not null)
            {
                try
                {
                    await _client.Containers.StopContainerAsync(record.ContainerId, new ContainerStopParameters(), ct);
                }
                catch (DockerApiException)
                {
                    // Already stopped/gone — fall through to remove, which is the part that must succeed.
                }

                await _client.Containers.RemoveContainerAsync(
                    record.ContainerId, new ContainerRemoveParameters { Force = true }, ct);

                _sandboxes.TryRemove(request.SandboxId, out _);
                _portAllocator.Release(record.Ports);

                if (request.DeleteFiles && Directory.Exists(record.HostPath))
                    Directory.Delete(record.HostPath, recursive: true);
            }

            return new DestroySandboxResponse(request.RequestId, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to destroy sandbox {SandboxId}", request.SandboxId);
            return new DestroySandboxResponse(request.RequestId, false, ex.Message);
        }
    }

    public async Task<ListSandboxesResponse> ListAsync(ListSandboxesRequest request, CancellationToken ct)
    {
        var containers = await ListLabelledContainersAsync(ct);
        var adopted = new List<AdoptedSandbox>();

        foreach (var container in containers)
        {
            if (!container.Labels.TryGetValue(LabelId, out var idText) || !Guid.TryParse(idText, out var sandboxId))
                continue;

            var createdAt = container.Labels.TryGetValue(LabelCreatedAt, out var createdText) &&
                DateTimeOffset.TryParse(createdText, out var parsed)
                ? parsed
                : container.Created;

            adopted.Add(new AdoptedSandbox(sandboxId, container.ID, container.State, createdAt));

            // Seeded regardless of whether the owner label parses below, so a still-running container
            // this broker instance has never seen before (its own restart, or another broker's leftover)
            // never gets its ports handed to a brand-new sandbox.
            var containerPorts = ParsePortsLabel(container.Labels);
            _portAllocator.Seed(containerPorts);

            if (container.Labels.TryGetValue(LabelOwner, out var ownerText) && Guid.TryParse(ownerText, out var ownerId))
            {
                var kind = container.Labels.TryGetValue(LabelKind, out var kindText) &&
                    Enum.TryParse<SandboxKind>(kindText, ignoreCase: true, out var parsedKind)
                    ? parsedKind
                    : SandboxKind.Session;
                _sandboxes[sandboxId] = new SandboxRecord(ownerId, container.ID, Path.Combine(_settings.SandboxRootPath, ownerText), kind, containerPorts);
            }
        }

        return new ListSandboxesResponse(request.RequestId, adopted);
    }

    /// <summary>Resolves a SandboxId to its owner/container/host-path, falling back to a Docker label
    /// lookup when this process hasn't seen it yet (e.g. right after a broker restart, before the API's
    /// next "ensure" call repopulates the cache). Used by the frame dispatcher to find the OwnerId a
    /// FileOpRequest needs — file ops carry only a SandboxId, not an OwnerId, in the wire protocol.</summary>
    public async Task<Guid?> ResolveOwnerIdAsync(Guid sandboxId, CancellationToken ct)
    {
        var record = await ResolveAsync(sandboxId, ct);
        return record?.OwnerId;
    }

    private async Task<SandboxRecord?> ResolveAsync(Guid sandboxId, CancellationToken ct)
    {
        if (_sandboxes.TryGetValue(sandboxId, out var cached))
            return cached;

        var containers = await ListLabelledContainersAsync(ct, sandboxId);
        var container = containers.FirstOrDefault();
        if (container is null)
            return null;

        if (!container.Labels.TryGetValue(LabelOwner, out var ownerText) || !Guid.TryParse(ownerText, out var ownerId))
            return null;

        var kind = container.Labels.TryGetValue(LabelKind, out var kindText) &&
            Enum.TryParse<SandboxKind>(kindText, ignoreCase: true, out var parsedKind)
            ? parsedKind
            : SandboxKind.Session;

        var ports = ParsePortsLabel(container.Labels);
        _portAllocator.Seed(ports);

        var record = new SandboxRecord(ownerId, container.ID, Path.Combine(_settings.SandboxRootPath, ownerText), kind, ports);
        _sandboxes[sandboxId] = record;
        return record;
    }

    private async Task<IList<ContainerListResponse>> ListLabelledContainersAsync(CancellationToken ct, Guid? sandboxId = null)
    {
        var labelFilter = sandboxId is null
            ? $"{LabelSandbox}=true"
            : $"{LabelId}={sandboxId}";

        return await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [labelFilter] = true },
            },
        }, ct);
    }

    private async Task<ContainerListResponse?> FindContainerByNameAsync(string name, CancellationToken ct)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [name] = true },
            },
        }, ct);

        // Docker's name filter is a substring match, so confirm an exact match (Names are prefixed with "/").
        return containers.FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == name));
    }

    private async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await _client.Images.InspectImageAsync(image, ct);
        }
        catch (DockerImageNotFoundException)
        {
            using var pullCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.ImagePullTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pullCts.Token);
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = image },
                authConfig: null,
                progress: new Progress<JSONMessage>(),
                linked.Token);
        }
    }

    /// <summary>Parses the comma-separated LabelPorts value a container was created with. Empty (not an
    /// error) for a container from before this feature existed, or one whose label is missing/malformed
    /// for any other reason -- callers treat "no ports known" as a legitimate, if unfortunate, state.</summary>
    private static IReadOnlyList<int> ParsePortsLabel(IDictionary<string, string> labels)
    {
        if (!labels.TryGetValue(LabelPorts, out var raw) || string.IsNullOrWhiteSpace(raw))
            return [];

        var ports = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out var port)) ports.Add(port);
        }
        return ports;
    }

    /// <summary>Best-effort live check: binds and immediately closes a TCP listener on the port, with
    /// SO_REUSEADDR off (.NET turns it on by default on Unix, which lets the test bind succeed on a port a
    /// connected or TIME_WAIT socket still holds -- one Docker's own bind then fails on) and dual-stack so
    /// a [::] listener counts too. Still only a point-in-time probe, not a reservation: something can grab
    /// the port before the container starts, which is what CreateAsync's retry is for. It is also only
    /// meaningful when the broker runs directly on the Docker host; inside a container it probes the
    /// container's own network, and ListDockerPublishedPortsAsync is what keeps sandboxes off ports other
    /// containers publish.</summary>
    private static bool IsPortFree(int port)
    {
        try
        {
            return TryBindExclusively(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, port);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.AccessDenied))
        {
            // IPv6 unavailable on this host (disabled, or no dual-stack) -- an IPv4 probe is still valid.
            try
            {
                return TryBindExclusively(AddressFamily.InterNetwork, IPAddress.Any, port);
            }
            catch (SocketException)
            {
                return false;
            }
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool TryBindExclusively(AddressFamily family, IPAddress address, int port)
    {
        using var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        if (family == AddressFamily.InterNetworkV6)
            socket.DualMode = true;
        socket.Bind(new IPEndPoint(address, port));
        socket.Listen();
        return true;
    }

    private static string ContainerName(Guid sandboxId) => $"personalai-sbx-{sandboxId.ToString("N")[..8]}";

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record SandboxRecord(Guid OwnerId, string ContainerId, string HostPath, SandboxKind Kind, IReadOnlyList<int> Ports);
}
