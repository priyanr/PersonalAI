namespace PersonalAI.Core.Models.Settings;

public class NetworkScanSettings
{
    public const string SectionName = "NetworkScan";

    /// <summary>CIDR ranges targets must fall entirely within. Default: RFC1918 + link-local + loopback.
    /// A target outside every one of these is refused before any socket is opened.</summary>
    public string[] AllowedRanges { get; set; } =
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "127.0.0.0/8"
    ];

    /// <summary>Hard cap on hosts a single call may sweep (one /24 by default). A CIDR exceeding this
    /// is rejected outright — never silently truncated.</summary>
    public int MaxHosts { get; set; } = 256;

    public int MaxConcurrency { get; set; } = 32;

    /// <summary>Timeout for ICMP echo, TCP connect, and UDP receive-wait.</summary>
    public int ProbeTimeoutMs { get; set; } = 750;

    /// <summary>Best-effort read window after a TCP connect succeeds, for banner grabbing.</summary>
    public int BannerReadTimeoutMs { get; set; } = 500;

    public int MaxBannerBytes { get; set; } = 256;

    public int[] DefaultTcpPorts { get; set; } =
        [21, 22, 23, 25, 53, 80, 110, 111, 135, 139, 143, 443, 445, 993, 995, 1433, 1723, 3306, 3389, 5432, 5900, 8080, 8443];

    public int[] DefaultUdpPorts { get; set; } =
        [53, 67, 68, 69, 123, 137, 138, 161, 162, 500, 514, 1900, 5353];

    public int[] DefaultHttpsPorts { get; set; } = [443, 8443];

    /// <summary>When true, network and broadcast addresses (e.g. .0/.255 in a /24) are skipped during
    /// CIDR expansion — they can never be live hosts. Only applies to prefix lengths &lt;= 30.</summary>
    public bool ExcludeNetworkAndBroadcastAddresses { get; set; } = true;

    /// <summary>Overall watchdog so a single call can never stall a chat turn indefinitely. Remaining
    /// in-flight probes are cancelled and the result is reported as partial.</summary>
    public int MaxTotalDurationSeconds { get; set; } = 120;
}
