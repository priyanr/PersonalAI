namespace PersonalAI.SandboxHost;

/// <summary>Config for this broker process, bound from the "Broker" section — appsettings.json,
/// docker-compose environment (Broker__Key=Value), or the systemd unit's Environment= lines, following
/// the same Section__Key convention the rest of the repo uses. Distinct from PersonalAI.Core's
/// SandboxSettings: that section configures the API side (which tools are exposed, which images are
/// allow-listed); this one configures where THIS broker instance lives and how it talks to Docker.</summary>
public sealed class BrokerSettings
{
    public const string SectionName = "Broker";

    /// <summary>The API's sandbox WebSocket endpoint, e.g. "wss://api.example.com/api/sandbox/host"
    /// in production or "ws://localhost:5080/api/sandbox/host" for local dev.</summary>
    public string ApiUrl { get; set; } = "ws://localhost:5080/api/sandbox/host";

    /// <summary>Shared secret sent as the X-Sandbox-Host-Key header. Must match the API's Sandbox:HostKey
    /// exactly — never sent as a query string, since that would land in access logs.</summary>
    public string HostKey { get; set; } = "";

    /// <summary>Identifies this broker instance to the API (surfaced in /api/sandbox/status). Defaults to
    /// the machine name so a bare docker-compose/systemd deploy needs no configuration to get a sane value.</summary>
    public string HostId { get; set; } = Environment.MachineName;

    /// <summary>Root directory on THIS HOST under which every owner gets "{SandboxRootPath}/{ownerId}",
    /// bind-mounted at /work in that owner's container. Must be the identical path the Docker daemon
    /// itself sees — see the README's bind-mount gotcha if this broker itself runs containerised.</summary>
    public string SandboxRootPath { get; set; } = "/data/sandbox";

    /// <summary>Passed to DockerClientBuilder.WithEndpoint. The default is the standard Unix socket path;
    /// override to "npipe://./pipe/docker_engine" for local Windows dev against Docker Desktop.</summary>
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>Timeout for a single Docker API call (create/start/inspect/list/stop/remove) and for
    /// the HostHello/HostHelloAck handshake round trip.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>How long the ClientWebSocket waits for a Pong after a keep-alive Ping before aborting the
    /// connection. Without this, a connection that dies silently on the wire (sleep/resume, a network
    /// change, an idle NAT mapping expiring) leaves ReceiveAsync hanging forever with no signal to
    /// reconnect from — the API's own heartbeat timeout only helps if its abort can still reach us.</summary>
    public int KeepAliveTimeoutSeconds { get; set; } = 45;

    /// <summary>First use of an image may need to pull it — this gets its own, longer timeout.</summary>
    public int ImagePullTimeoutSeconds { get; set; } = 300;

    /// <summary>Mirrors PersonalAI.Core's SandboxSettings.MaxListEntries — kept as the broker's own copy
    /// since this project cannot reference Core. Caps how many FileEntry rows a single List op returns.</summary>
    public int MaxListEntries { get; set; } = 500;

    /// <summary>Mirrors SandboxSettings.MaxSearchHits — caps how many SearchHit rows a single Search op returns.</summary>
    public int MaxSearchHits { get; set; } = 200;

    /// <summary>Mirrors SandboxSettings.MaxFileTransferBytes — caps a single Read/Write's payload.</summary>
    public long MaxFileTransferBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Mirrors SandboxSettings.MaxDownloadBytes — caps a single ReadRaw's payload. Larger than
    /// MaxFileTransferBytes since a download isn't constrained by the model's context budget the way a
    /// Read result is; unlike Read/Write it must never silently truncate (a truncated binary is corrupt),
    /// so an oversized file fails outright instead.</summary>
    public long MaxDownloadBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Mirrors SandboxSettings.MaxZipBytes — caps the total UNCOMPRESSED size of the files a
    /// single Zip op will archive, checked while walking the selection, before building the archive.</summary>
    public long MaxZipBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Raw-bytes safety net per exec stream (stdout/stderr), independent of and in addition to
    /// the API-side per-stream char cap applied when it renders the tool result — this one exists purely
    /// so a runaway command can't make the broker buffer unbounded memory while streaming chunks.</summary>
    public long MaxExecOutputBytesPerStream { get; set; } = 5 * 1024 * 1024;

    /// <summary>Size of each streamed ExecOutputChunk before base64 inflation.</summary>
    public int ExecChunkBytes { get; set; } = 32 * 1024;

    /// <summary>How many ports each sandbox container gets published — host port == container port, TCP —
    /// so a server the model starts inside it is reachable from outside without the model having to ask
    /// for a specific port to be opened.</summary>
    public int ExposedPortCount { get; set; } = 5;

    /// <summary>The range ports are chosen from. Deliberately below every OS ephemeral range (Linux
    /// 32768–60999 per /proc/sys/net/ipv4/ip_local_port_range, Windows 49152–65535): ports in those ranges
    /// get picked for any outbound connection on the host, so a sandbox could be handed a port that some
    /// socket grabs moments later and Docker's bind fails with "address already in use". Values are still
    /// checked against a live bind test, Docker's published ports and the host's active TCP ports before
    /// being handed out, on top of an in-memory allocated-set that survives across sandboxes created while
    /// this broker process is running.</summary>
    public int HostPortRangeStart { get; set; } = 20000;
    public int HostPortRangeEnd { get; set; } = 29999;
}
