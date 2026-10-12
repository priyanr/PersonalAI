using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Tools;

/// <summary>Passive/standard LAN recon: ICMP host discovery, TCP connect scan with best-effort banner
/// grab, UDP probe, and TLS/certificate inspection. Every target is validated against a configured
/// private-address allow-list before any socket is opened — out-of-scope targets are refused with a
/// ToolCompleted(IsError:true), never scanned and never routed through ToolApprovalRequired. There is
/// no "maybe" for scope: this tool has no workspace-sandbox concept to escape, so that mechanism
/// (used by the file tools) does not apply here.</summary>
public sealed class NetworkScanTool : ITool
{
    private static readonly string[] AllProtocols = ["icmp", "tcp", "udp", "https"];

    private readonly NetworkScanSettings _settings;
    private readonly IReadOnlyList<CidrRange> _allowedRanges;

    public NetworkScanTool(IOptions<NetworkScanSettings> settings)
    {
        _settings = settings.Value;
        _allowedRanges = [.. _settings.AllowedRanges.Select(CidrRange.Parse)];
    }

    public string Name => "network_scan";

    public string Description => """
        Scan hosts on your own private/local network (LAN) for open ports, live status, and TLS
        certificate info. Restricted to private-use address space only (RFC1918, link-local, loopback,
        or an operator-configured allow-list) — any other target is refused with an explanation, it is
        never scanned.

        target: a single IPv4 address ("192.168.1.10") or CIDR range ("192.168.1.0/24"). A CIDR may
        cover at most 256 hosts (one /24) per call; larger ranges are rejected — narrow the range and
        call again.

        protocols (default: all): "icmp" (ping/host discovery — cheap, always start a subnet sweep with
        this alone), "tcp" (TCP connect scan with best-effort banner grab, no data sent beyond the
        handshake), "udp" (sends a small benign probe per port, classifies open / open|filtered / closed
        from the response or ICMP unreachable), "https" (TLS handshake to inspect certificate
        subject/issuer/validity/expiry, negotiated TLS version, and presence of common security response
        headers).

        Workflow: for a whole subnet, first call with protocols: ["icmp"] to find which hosts are up
        cheaply, then make follow-up calls with target set to individual live IPs and protocols including
        tcp/udp/https for detailed scanning — this keeps each call fast and keeps your iteration budget
        low. Only hosts that responded, or had a notable finding, are included in the output.

        This tool performs no credential guessing, exploitation, or payload sending — passive/standard
        recon only. It does not modify anything on the scanned hosts.
        """;

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "target": {
              "type": "string",
              "description": "Single IPv4 address (e.g. 192.168.1.10) or CIDR range (e.g. 192.168.1.0/24) on your private network. Public/non-private addresses are refused."
            },
            "protocols": {
              "type": "array",
              "items": { "type": "string", "enum": ["icmp", "tcp", "udp", "https"] },
              "description": "Which probes to run. Defaults to all four if omitted."
            },
            "tcpPorts": {
              "type": "array",
              "items": { "type": "integer", "minimum": 1, "maximum": 65535 },
              "description": "TCP ports to connect-scan. Defaults to a curated list of common ports."
            },
            "udpPorts": {
              "type": "array",
              "items": { "type": "integer", "minimum": 1, "maximum": 65535 },
              "description": "UDP ports to probe. Defaults to a curated list of common UDP services."
            },
            "httpsPorts": {
              "type": "array",
              "items": { "type": "integer", "minimum": 1, "maximum": 65535 },
              "description": "Ports to run TLS/certificate inspection on when 'https' is included in protocols. Defaults to [443, 8443]."
            }
          },
          "required": ["target"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var targetArg = arguments.GetStringArg("target") ?? throw new ArgumentException("'target' is required.");

        var protocolNames = arguments.GetStringArrayArg("protocols");
        var protocols = protocolNames is { Count: > 0 }
            ? new HashSet<string>(protocolNames, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(AllProtocols, StringComparer.OrdinalIgnoreCase);

        var unknown = protocols.Where(p => Array.IndexOf(AllProtocols, p.ToLowerInvariant()) < 0).ToList();
        if (unknown.Count > 0)
            return new ToolCompleted($"Unknown protocol(s): {string.Join(", ", unknown)}. Must be one of icmp, tcp, udp, https.", IsError: true);

        var tcpPorts = arguments.GetIntArrayArg("tcpPorts") ?? _settings.DefaultTcpPorts;
        var udpPorts = arguments.GetIntArrayArg("udpPorts") ?? _settings.DefaultUdpPorts;
        var httpsPorts = arguments.GetIntArrayArg("httpsPorts") ?? _settings.DefaultHttpsPorts;

        if (!CidrRange.TryParse(targetArg, out var range))
            return new ToolCompleted($"Could not parse '{targetArg}' as an IPv4 address or CIDR range.", IsError: true);

        if (!_allowedRanges.Any(range.IsContainedIn))
            return new ToolCompleted(
                $"Target '{targetArg}' is outside the allowed private-network ranges ({string.Join(", ", _allowedRanges)}). " +
                "This tool only scans your own private/local network.", IsError: true);

        if (range.HostCount > (ulong)_settings.MaxHosts)
            return new ToolCompleted(
                $"Requested range covers {range.HostCount} addresses, which exceeds the limit of {_settings.MaxHosts} per call. " +
                "Narrow the CIDR (e.g. to /24 or smaller) and try again.", IsError: true);

        var hosts = ExpandHosts(range).ToList();
        if (hosts.Count == 0)
            return new ToolCompleted("No usable host addresses in the requested range.");

        var results = new ConcurrentDictionary<IPAddress, HostResult>();
        foreach (var h in hosts) results[h] = new HostResult();

        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.MaxTotalDurationSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
        var semaphore = new SemaphoreSlim(_settings.MaxConcurrency);
        var tasks = new List<Task>();

        foreach (var host in hosts)
        {
            var hostResult = results[host];
            if (protocols.Contains("icmp"))
                tasks.Add(RunProbeAsync(semaphore, linked.Token, () => ProbeIcmpAsync(host, hostResult, linked.Token)));
            if (protocols.Contains("tcp"))
                foreach (var port in tcpPorts)
                    tasks.Add(RunProbeAsync(semaphore, linked.Token, () => ProbeTcpAsync(host, port, hostResult, linked.Token)));
            if (protocols.Contains("udp"))
                foreach (var port in udpPorts)
                    tasks.Add(RunProbeAsync(semaphore, linked.Token, () => ProbeUdpAsync(host, port, hostResult, linked.Token)));
            if (protocols.Contains("https"))
                foreach (var port in httpsPorts)
                    tasks.Add(RunProbeAsync(semaphore, linked.Token, () => ProbeHttpsAsync(host, port, hostResult, linked.Token)));
        }

        var truncated = false;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            truncated = true;
        }
        ct.ThrowIfCancellationRequested();

        return new ToolCompleted(FormatResults(targetArg, protocols, hosts.Count, results, truncated));
    }

    private static async Task RunProbeAsync(SemaphoreSlim semaphore, CancellationToken ct, Func<Task> probe)
    {
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await probe().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Individual probe failures are recorded inside the probe itself where meaningful (e.g. a
            // classified port state); anything unexpected is swallowed here so one bad probe can't
            // fault the whole scan.
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ProbeIcmpAsync(IPAddress host, HostResult result, CancellationToken ct)
    {
        using var ping = new Ping();
        PingReply reply;
        try
        {
            reply = await ping.SendPingAsync(host, _settings.ProbeTimeoutMs).WaitAsync(ct);
        }
        catch (PingException)
        {
            return;
        }

        if (reply.Status == IPStatus.Success)
            result.IcmpRoundtripMs = reply.RoundtripTime;
    }

    private async Task ProbeTcpAsync(IPAddress host, int port, HostResult result, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(host, port, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(_settings.ProbeTimeoutMs), ct);
        }
        catch (TimeoutException)
        {
            return; // connect attempt timed out — ambiguous/filtered, not reported
        }
        catch (SocketException)
        {
            return; // refused/unreachable — treat as closed, omit from output
        }

        string? banner = null;
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[_settings.MaxBannerBytes];
            var readTask = stream.ReadAsync(buffer, ct).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(_settings.BannerReadTimeoutMs, ct));
            if (completed == readTask && readTask.IsCompletedSuccessfully && readTask.Result > 0)
                banner = SanitizeBanner(Encoding.Latin1.GetString(buffer, 0, readTask.Result));
        }
        catch
        {
            // banner grab is best-effort only
        }

        result.TcpResults.Add(new PortResult(port, "open", banner));
    }

    private async Task ProbeUdpAsync(IPAddress host, int port, HostResult result, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Connect(host, port);
            await udp.SendAsync(new byte[] { 0 }, ct);
        }
        catch (SocketException)
        {
            result.UdpResults.Add(new PortResult(port, "closed", null));
            return;
        }

        try
        {
            var receiveTask = udp.ReceiveAsync(ct).AsTask();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(_settings.ProbeTimeoutMs, ct));
            result.UdpResults.Add(completed == receiveTask && receiveTask.IsCompletedSuccessfully
                ? new PortResult(port, "open", null)
                : new PortResult(port, "open|filtered", null));
        }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionReset)
        {
            // A connected UDP socket surfaces an ICMP Port Unreachable this way.
            result.UdpResults.Add(new PortResult(port, "closed", null));
        }
        catch (SocketException)
        {
            result.UdpResults.Add(new PortResult(port, "open|filtered", null));
        }
    }

    private async Task ProbeHttpsAsync(IPAddress host, int port, HostResult result, CancellationToken ct)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(host, port, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(_settings.ProbeTimeoutMs), ct);
        }
        catch (TimeoutException) { return; }
        catch (SocketException) { return; }

        try
        {
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host.ToString(),
                EnabledSslProtocols = SslProtocols.None
            }, ct).WaitAsync(TimeSpan.FromMilliseconds(_settings.ProbeTimeoutMs), ct);

            var cert = ssl.RemoteCertificate as X509Certificate2;
            var tlsVersion = ssl.SslProtocol.ToString();

            var missingHeaders = new List<string>();
            try
            {
                var request = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(request, ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(_settings.BannerReadTimeoutMs), ct);

                var headerText = await ReadHeadersAsync(ssl, ct);
                foreach (var header in new[] { "Strict-Transport-Security", "X-Content-Type-Options", "X-Frame-Options", "Content-Security-Policy" })
                    if (!headerText.Contains(header, StringComparison.OrdinalIgnoreCase))
                        missingHeaders.Add(header);
            }
            catch
            {
                // header check is best-effort only
            }

            result.HttpsResults.Add(new HttpsResult(
                port, cert?.Subject, cert?.Issuer, cert?.NotBefore ?? DateTime.MinValue, cert?.NotAfter ?? DateTime.MinValue,
                tlsVersion, missingHeaders));
        }
        catch (AuthenticationException) { }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    private async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow.AddMilliseconds(_settings.BannerReadTimeoutMs);

        while (DateTime.UtcNow < deadline && !sb.ToString().Contains("\r\n\r\n"))
        {
            var readTask = stream.ReadAsync(buffer, ct).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(_settings.BannerReadTimeoutMs, ct));
            if (completed != readTask || !readTask.IsCompletedSuccessfully || readTask.Result <= 0)
                break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, readTask.Result));
        }

        return sb.ToString();
    }

    private IEnumerable<IPAddress> ExpandHosts(CidrRange range)
    {
        var hostCount = range.HostCount;
        var exclude = _settings.ExcludeNetworkAndBroadcastAddresses && hostCount >= 4;
        for (var i = 0UL; i < hostCount; i++)
        {
            if (exclude && (i == 0 || i == hostCount - 1)) continue;
            yield return CidrRange.UIntToIp(range.BaseUint + (uint)i);
        }
    }

    private static string SanitizeBanner(string raw)
    {
        var cleaned = new string([.. raw.Where(c => !char.IsControl(c) || c is '\r' or '\n')]).Trim();
        cleaned = cleaned.Replace("\r", "").Replace("\n", " ");
        return cleaned.Length > 200 ? cleaned[..200] + "..." : cleaned;
    }

    private static string FormatResults(
        string targetArg, HashSet<string> protocols, int hostsInRange,
        ConcurrentDictionary<IPAddress, HostResult> results, bool truncated)
    {
        var sb = new StringBuilder();
        var withFindings = results
            .Where(kv => kv.Value.HasFindings)
            .OrderBy(kv => CidrRange.IpToUInt(kv.Key))
            .ToList();

        sb.AppendLine($"network_scan target={targetArg} protocols=[{string.Join(",", protocols)}] hosts_in_range={hostsInRange} hosts_with_findings={withFindings.Count}");
        if (truncated)
            sb.AppendLine("[scan truncated: time budget exceeded, results are partial]");

        if (withFindings.Count == 0)
        {
            sb.AppendLine("No hosts responded / no open ports found among the requested probes.");
            return sb.ToString();
        }

        foreach (var (host, r) in withFindings)
        {
            sb.AppendLine();
            sb.AppendLine($"Host {host}:");
            if (r.IcmpRoundtripMs is { } rtt)
                sb.AppendLine($"  icmp: up ({rtt}ms)");

            foreach (var p in r.TcpResults.OrderBy(p => p.Port))
            {
                var flag = TcpRiskFlag(p.Port);
                var bannerPart = p.Banner is null ? "" : $" banner=\"{p.Banner}\"";
                var flagPart = flag is null ? "" : $" [{flag}]";
                sb.AppendLine($"  tcp/{p.Port}: {p.Status}{bannerPart}{flagPart}");
            }

            foreach (var p in r.UdpResults.OrderBy(p => p.Port))
                sb.AppendLine($"  udp/{p.Port}: {p.Status}");

            foreach (var h in r.HttpsResults.OrderBy(h => h.Port))
            {
                sb.AppendLine($"  https/{h.Port}: tls={h.TlsVersion} subject=\"{h.Subject}\" issuer=\"{h.Issuer}\" notAfter={h.NotAfter:yyyy-MM-dd}");
                foreach (var flag in HttpsRiskFlags(h))
                    sb.AppendLine($"    [{flag}]");
                if (h.MissingHeaders.Count > 0)
                    sb.AppendLine($"    missing security headers: {string.Join(", ", h.MissingHeaders)}");
            }
        }

        return sb.ToString();
    }

    private static string? TcpRiskFlag(int port) => port switch
    {
        23 => "telnet exposed — unencrypted remote access",
        21 => "FTP exposed — often unencrypted credentials",
        139 or 445 => "SMB exposed — verify SMBv1 is disabled",
        3389 => "RDP exposed — restrict to trusted hosts/VPN",
        _ => null
    };

    private static IEnumerable<string> HttpsRiskFlags(HttpsResult h)
    {
        if (h.NotAfter != DateTime.MinValue)
        {
            if (h.NotAfter < DateTime.UtcNow) yield return "certificate EXPIRED";
            else if (h.NotAfter < DateTime.UtcNow.AddDays(30)) yield return "certificate expiring soon";
        }
        if (!string.IsNullOrEmpty(h.Subject) && h.Subject == h.Issuer) yield return "self-signed certificate";
        if (h.TlsVersion is "Ssl2" or "Ssl3" or "Tls" or "Tls11") yield return $"weak TLS version negotiated ({h.TlsVersion})";
    }

    private sealed class HostResult
    {
        public long? IcmpRoundtripMs;
        public readonly ConcurrentBag<PortResult> TcpResults = [];
        public readonly ConcurrentBag<PortResult> UdpResults = [];
        public readonly ConcurrentBag<HttpsResult> HttpsResults = [];

        public bool HasFindings => IcmpRoundtripMs is not null || !TcpResults.IsEmpty || !UdpResults.IsEmpty || !HttpsResults.IsEmpty;
    }

    private sealed record PortResult(int Port, string Status, string? Banner);

    private sealed record HttpsResult(
        int Port, string? Subject, string? Issuer, DateTime NotBefore, DateTime NotAfter,
        string TlsVersion, IReadOnlyList<string> MissingHeaders);

    /// <summary>A hand-rolled IPv4 CIDR range (base address + prefix length) with its own containment
    /// math, deliberately not relying on System.Net.IPNetwork's normalization semantics for
    /// non-network-aligned input (e.g. "192.168.1.5/24") — the base is always masked down to the true
    /// network address at parse time.</summary>
    private readonly record struct CidrRange(uint BaseUint, int PrefixLength)
    {
        public ulong HostCount => PrefixLength == 0 ? 1UL << 32 : 1UL << (32 - PrefixLength);

        public bool Contains(uint addressUint) => (addressUint & Mask(PrefixLength)) == BaseUint;

        /// <summary>True when this range is fully contained within <paramref name="outer"/> — this
        /// range's prefix is at least as specific, and its base address falls inside the outer range.</summary>
        public bool IsContainedIn(CidrRange outer) => PrefixLength >= outer.PrefixLength && outer.Contains(BaseUint);

        public static CidrRange Parse(string cidr) =>
            TryParse(cidr, out var range) ? range : throw new FormatException($"'{cidr}' is not a valid IPv4 CIDR range.");

        public static bool TryParse(string text, out CidrRange range)
        {
            range = default;
            var parts = text.Split('/');
            string addressPart;
            int prefixLength;

            if (parts.Length == 2)
            {
                addressPart = parts[0];
                if (!int.TryParse(parts[1], out prefixLength) || prefixLength is < 0 or > 32)
                    return false;
            }
            else if (parts.Length == 1)
            {
                addressPart = parts[0];
                prefixLength = 32;
            }
            else
            {
                return false;
            }

            if (!IPAddress.TryParse(addressPart, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                return false;

            range = new CidrRange(IpToUInt(address) & Mask(prefixLength), prefixLength);
            return true;
        }

        public static uint IpToUInt(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        }

        public static IPAddress UIntToIp(uint value) =>
            new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

        private static uint Mask(int prefixLength) => prefixLength == 0 ? 0u : unchecked(uint.MaxValue << (32 - prefixLength));

        public override string ToString() => $"{UIntToIp(BaseUint)}/{PrefixLength}";
    }
}
