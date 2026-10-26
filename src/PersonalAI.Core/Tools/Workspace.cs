using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Tools;

public readonly record struct WorkspacePathResolution(string FullPath, bool IsInsideWorkspace, bool LooksLikeSandboxPath);

/// <summary>Confines file operations to a root directory by default. Model-supplied paths are
/// untrusted input: resolve to canonical form and report whether they escape the root — the
/// caller (each ITool) decides whether escaping requires user approval.</summary>
public sealed class Workspace(IOptions<WorkspaceSettings> settings)
{
    private static readonly string[] IgnoredDirectories = [".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "__pycache__"];

    public string Root { get; } = Path.GetFullPath(settings.Value.RootPath);

    public WorkspacePathResolution Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.");

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        var relative = Path.GetRelativePath(Root, full);
        var isInside = relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);

        return new WorkspacePathResolution(full, isInside, LooksLikeSandboxWorkPath(path));
    }

    public string ToRelative(string fullPath)
    {
        return Path.GetRelativePath(Root, fullPath);
    }

    public static bool IsIgnoredDirectory(string directoryName)
    {
        return IgnoredDirectories.Contains(directoryName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the model-supplied path is rooted at (or under) "/work" — the sandbox tool's
    /// mount point inside its own container, never this workspace (Workspace:RootPath is "./data/workspace"
    /// locally, "/workspace" in Docker/k8s -- never "/work"). A model that has used the sandbox tool and
    /// internalized "paths are relative to /work" sometimes calls one of THESE workspace-confined tools by
    /// the same action name with a /work-rooted path by mistake -- there is no deployment where that path
    /// legitimately belongs here, so every in-process file tool checks this before falling back to the
    /// ordinary outside-workspace approval flow, and refuses outright with a pointer to the right tool
    /// instead of pausing the whole conversation for a human to approve something that's always wrong.</summary>
    public static bool LooksLikeSandboxWorkPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return false;
        var normalized = path.Replace('\\', '/');
        return normalized.Equals("/work", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/work/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shared corrective message every in-process file tool returns for a LooksLikeSandboxPath
    /// hit, instead of the ordinary outside-workspace approval prompt.</summary>
    public static string SandboxPathHint(string path)
    {
        return $"'{path}' looks like it was meant for the sandbox tool's /work directory, not this " +
        "workspace-confined tool — they are different filesystems on different machines. Use the " +
        "sandbox tool's own read_file/write_file/edit_file/list_files/search_files actions for files " +
        "inside the sandbox instead.";
    }
}
