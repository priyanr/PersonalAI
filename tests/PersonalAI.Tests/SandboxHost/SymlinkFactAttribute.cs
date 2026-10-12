namespace PersonalAI.Tests.SandboxHost;

/// <summary>A [Fact] for tests that need to create a real symbolic link. Creating one on Windows requires
/// Developer Mode or elevation, so on a machine without it these tests would otherwise return early and
/// report a green pass having asserted nothing — the worst outcome for a security test. This reports them
/// as explicitly Skipped instead, so the escape-resistance coverage is visibly absent rather than
/// silently fake. The broker itself runs on a Linux Docker host, where symlinks always work and these
/// tests therefore always execute.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    private static readonly bool Supported = ProbeSymlinkSupport();

    public SymlinkFactAttribute()
    {
        if (!Supported)
            Skip = "Creating symbolic links is not permitted on this machine (Windows needs Developer Mode or elevation).";
    }

    private static bool ProbeSymlinkSupport()
    {
        var probe = Path.Combine(Path.GetTempPath(), "personalai-symlink-probe-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(probe, "target");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(Path.Combine(probe, "link"), target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try { if (Directory.Exists(probe)) Directory.Delete(probe, recursive: true); } catch { /* best effort */ }
        }
    }
}
