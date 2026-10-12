namespace PersonalAI.SandboxHost;

/// <summary>The security boundary for every file operation the broker performs. Every model-supplied path
/// arrives relative to the sandbox's "/work" as far as the model is concerned, but on the host side it
/// must resolve inside "{SandboxRootPath}/{ownerId}" and nowhere else. This ports the canonicalize-and-
/// check-escape technique from PersonalAI.Core.Tools.Workspace.Resolve (this project cannot reference
/// Core, so the logic is duplicated rather than shared) with one deliberate difference: Workspace allows
/// an absolute/rooted path through and merely flags it as "outside the workspace" for the caller to ask
/// approval for, because the in-process tools have an approval flow. The sandbox has no user to approve
/// anything mid-run, so here a rooted path is rejected outright rather than let through for the caller to
/// judge.</summary>
public sealed class SandboxPathResolver(string sandboxRootPath)
{
    /// <summary>The per-owner root, e.g. "/data/sandbox/3fa85f64-...". Created on demand by the caller
    /// (DockerSandboxManager.CreateAsync); this method only computes the path, it never touches disk.</summary>
    public string OwnerRoot(Guid ownerId) => Path.GetFullPath(Path.Combine(sandboxRootPath, ownerId.ToString()));

    /// <summary>Resolves <paramref name="path"/> against the owner's root and throws
    /// <see cref="UnauthorizedAccessException"/> if it is rooted/absolute or canonicalizes to anything
    /// outside that root (via "..", trailing dot/space tricks GetFullPath normalizes away, etc). The
    /// returned path is always the canonical form — callers must perform all I/O against ITS result, never
    /// against the raw input, or a canonicalization mismatch could reopen the exact class of bug this
    /// class exists to close.</summary>
    public string Resolve(Guid ownerId, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path must not be empty.", nameof(path));

        // A rooted path ("/etc/passwd", "C:\Windows", "\\server\share") has no legitimate meaning inside
        // a sandbox whose entire visible filesystem is "/work" — reject it outright rather than trying to
        // remap it, which is exactly the kind of "clever" handling that tends to hide an escape.
        if (Path.IsPathRooted(path))
            throw new UnauthorizedAccessException($"Path '{path}' must be relative to the sandbox root.");

        var root = OwnerRoot(ownerId);
        var full = Path.GetFullPath(Path.Combine(root, path));

        // First, the cheap lexical check: catches "..", which GetFullPath has already normalized away.
        EnsureInside(root, full, path);

        // Then the physical check. GetFullPath does NOT follow symlinks, so the lexical check alone is
        // not a boundary: the model can run `ln -s / /work/esc` inside its own container (which is root
        // with no dropped capabilities by default), and "esc/etc/shadow" then passes the check above
        // while pointing anywhere on the host. That matters because this file service runs on the DOCKER
        // HOST, not inside the container — a followed symlink would read, or with write_file overwrite,
        // arbitrary host files as whatever user the broker runs as. So resolve every existing component
        // to its final target and re-check containment against the equally-resolved root.
        var realFull = ResolveSymlinks(full, path);
        EnsureInside(ResolveSymlinks(root, path), realFull, path);

        // Return the RESOLVED path so callers do their I/O against the location we actually validated,
        // rather than re-walking the links and giving an attacker a window to swap one in between.
        return realFull;
    }

    private static void EnsureInside(string root, string candidate, string original)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var isInside = relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);

        if (!isInside)
            throw new UnauthorizedAccessException($"Path '{original}' escapes the sandbox root.");
    }

    /// <summary>Walks <paramref name="fullPath"/> component by component, replacing each existing segment
    /// that is a symbolic link with its final target. Components that do not exist yet (the common case
    /// for write_file creating a new file) have nothing to resolve and are appended as-is.</summary>
    private static string ResolveSymlinks(string fullPath, string original)
    {
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
            return fullPath;

        var current = pathRoot;
        foreach (var segment in fullPath[pathRoot.Length..]
                     .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current)
                : null;
            if (info is null)
                continue;

            try
            {
                // returnFinalTarget follows a chain of links; null means this segment is not a link.
                if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.GetFullPath(target.FullName);
            }
            catch (IOException ex)
            {
                // A broken or cyclic link can't be proven to stay inside the root, so refuse it rather
                // than fall back to the unresolved path.
                throw new UnauthorizedAccessException($"Path '{original}' could not be resolved safely.", ex);
            }
        }

        return current;
    }

    /// <summary>Inverse of <see cref="Resolve"/> — used to report paths back to the model relative to
    /// "/work" instead of leaking the real host path. The root is symlink-resolved here for the same
    /// reason Resolve resolves it: if the deployment puts the sandbox root behind a link (e.g. /data is
    /// itself a symlink), Resolve returns a fully-resolved path, and measuring it against an unresolved
    /// root would yield a nonsense "../.." relative path.</summary>
    public string ToSandboxRelative(Guid ownerId, string fullPath) =>
        Path.GetRelativePath(ResolveSymlinks(OwnerRoot(ownerId), fullPath), fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
}
