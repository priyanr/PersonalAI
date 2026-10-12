using System.Text.RegularExpressions;

namespace PersonalAI.SandboxHost;

/// <summary>Chooses which ports a new sandbox container publishes, and tracks which ports are currently
/// handed out so two sandboxes created around the same time never collide. Kept separate from
/// DockerSandboxManager, with the actual "is this port free" check passed in as a delegate, specifically
/// so the selection logic is unit-testable without a Docker daemon or real sockets — DockerSandboxManager
/// is the only caller that passes a real TCP bind-test.</summary>
public sealed class SandboxPortAllocator(int rangeStart, int rangeEnd, int count)
{
    private readonly object _gate = new();
    private readonly HashSet<int> _allocated = new();

    /// <summary>Marks ports as already spoken for without going through Allocate — used to seed the
    /// allocator from containers a restarted broker discovers still running (see
    /// DockerSandboxManager.ListAsync), so it never hands one of those out to a new sandbox. Adding an
    /// already-tracked port is a no-op.</summary>
    public void Seed(IEnumerable<int> portsInUse)
    {
        lock (_gate)
        {
            foreach (var port in portsInUse) _allocated.Add(port);
        }
    }

    /// <summary>Frees ports back to the pool — called once a sandbox using them is destroyed.</summary>
    public void Release(IEnumerable<int> ports)
    {
        lock (_gate)
        {
            foreach (var port in ports) _allocated.Remove(port);
        }
    }

    /// <summary>Picks exactly <c>count</c> ports: reuses as many of <paramref name="preferred"/> as are
    /// still free (so a container recreated after being orphaned keeps the ports a user may already be
    /// depending on), then fills any remainder with the lowest free ports in the configured range.
    /// Throws if the range is exhausted rather than silently returning fewer than requested — a caller
    /// getting back the wrong number of ports is a worse failure mode than a clear error up front.</summary>
    public IReadOnlyList<int> Allocate(IReadOnlyList<int> preferred, Func<int, bool> isFree)
    {
        lock (_gate)
        {
            var result = new List<int>(count);

            foreach (var port in preferred)
            {
                if (result.Count == count) break;
                if (_allocated.Contains(port) || result.Contains(port)) continue;
                if (!isFree(port)) continue;
                result.Add(port);
            }

            for (var port = rangeStart; port <= rangeEnd && result.Count < count; port++)
            {
                if (_allocated.Contains(port) || result.Contains(port)) continue;
                if (!isFree(port)) continue;
                result.Add(port);
            }

            if (result.Count < count)
            {
                throw new InvalidOperationException(
                    $"Could not find {count} free port(s) in range {rangeStart}-{rangeEnd} (found {result.Count}).");
            }

            foreach (var port in result) _allocated.Add(port);
            return result;
        }
    }

    /// <summary>True when a Docker create/start error means a published host port was already taken —
    /// the newer port mapper's "failed to bind host port …: address already in use" and the older
    /// docker-proxy's "Bind for …: port is already allocated". Only this failure is worth retrying with
    /// different ports; anything else would fail the same way again.</summary>
    public static bool IsPortConflict(string message) =>
        message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pulls the offending host port out of a port-conflict error, so a retry can drop just that
    /// one and keep the rest. Null when the message doesn't name one in a recognisable form.</summary>
    public static int? ParseConflictPort(string message)
    {
        var match = ConflictPortPattern.Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    // Lazy \S*? walks past the host address (0.0.0.0, [::], ...) up to the LAST ":<digits>" before the
    // optional /tcp — so an IPv6 address's own colons don't get mistaken for the port separator.
    private static readonly Regex ConflictPortPattern =
        new(@"(?:host port|Bind for)\s+\S*?:(\d+)(?:/tcp|/udp)?(?=[:\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
