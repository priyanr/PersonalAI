using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PersonalAI.Sandbox.Protocol;

namespace PersonalAI.SandboxHost;

/// <summary>Services every FileOp as plain System.IO against "{SandboxRootPath}/{ownerId}" — never
/// through Docker (no docker cp, no tar, no exec). This is the whole point of the host-bind-mount design:
/// the broker runs on the same machine as the files, so file operations are exactly as cheap as the
/// in-process tools' Workspace-scoped ones, just on a different root per owner instead of one shared
/// Workspace:RootPath.</summary>
public sealed class SandboxFileService(SandboxPathResolver resolver, IOptions<BrokerSettings> settings)
{
    private static readonly string[] IgnoredDirectories = [".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "__pycache__"];

    private readonly BrokerSettings _settings = settings.Value;

    public Task<FileOpResponse> ExecuteAsync(Guid ownerId, FileOpRequest request, CancellationToken ct)
    {
        try
        {
            return Task.FromResult(request.Op switch
            {
                FileOp.Read => Read(ownerId, request),
                FileOp.Write => Write(ownerId, request),
                FileOp.Edit => Edit(ownerId, request),
                FileOp.List => List(ownerId, request),
                FileOp.Search => Search(ownerId, request),
                FileOp.Delete => Delete(ownerId, request),
                FileOp.Move => Move(ownerId, request),
                FileOp.MakeDirectory => MakeDirectory(ownerId, request),
                FileOp.ReadRaw => ReadRaw(ownerId, request),
                FileOp.Zip => Zip(ownerId, request),
                _ => new FileOpResponse(request.RequestId, false, $"Unknown file op '{request.Op}'."),
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(new FileOpResponse(request.RequestId, false, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(new FileOpResponse(request.RequestId, false, ex.Message));
        }
    }

    private FileOpResponse Read(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        if (!File.Exists(path))
            return new FileOpResponse(request.RequestId, false, $"File not found: {request.Path}");

        var totalSize = new FileInfo(path).Length;
        var lines = File.ReadAllLines(path);

        IEnumerable<string> window = request.Offset > 0 ? lines.Skip(request.Offset) : lines;
        if (request.Limit > 0)
            window = window.Take(request.Limit);

        var text = string.Join('\n', window);
        var bytes = Encoding.UTF8.GetBytes(text);

        var cap = request.MaxBytes > 0 ? request.MaxBytes : _settings.MaxFileTransferBytes;
        var truncated = false;
        if (bytes.Length > cap)
        {
            bytes = bytes[..(int)cap];
            truncated = true;
        }

        return new FileOpResponse(request.RequestId, true, null,
            ContentBase64: Convert.ToBase64String(bytes), Truncated: truncated, TotalSize: totalSize);
    }

    /// <summary>Byte-exact read, unlike Read: no File.ReadAllLines/string.Join('\n') round trip, which
    /// decodes as text and would silently corrupt any real binary content. Never truncates on overflow —
    /// a truncated binary is corrupt, unlike a truncated text result returned for model context, so an
    /// oversized file fails outright instead.</summary>
    private FileOpResponse ReadRaw(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        if (!File.Exists(path))
            return new FileOpResponse(request.RequestId, false, $"File not found: {request.Path}");

        var totalSize = new FileInfo(path).Length;
        var cap = request.MaxBytes > 0 ? request.MaxBytes : _settings.MaxDownloadBytes;
        if (totalSize > cap)
            return new FileOpResponse(request.RequestId, false,
                $"{request.Path} is {totalSize} bytes, exceeding the {cap}-byte download limit.");

        var bytes = File.ReadAllBytes(path);
        return new FileOpResponse(request.RequestId, true, null,
            ContentBase64: Convert.ToBase64String(bytes), TotalSize: totalSize);
    }

    private FileOpResponse Write(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        if (string.IsNullOrEmpty(request.ContentBase64))
            return new FileOpResponse(request.RequestId, false, "Write requires content.");

        var bytes = Convert.FromBase64String(request.ContentBase64);
        // MaxBytes lets a caller override the default MaxFileTransferBytes cap (the sandbox file browser's
        // save-from-editor path does this, matching the larger MaxDownloadBytes cap it used to view the
        // file in the first place — a file you could open you must also be able to save back). The
        // model-facing write_file tool never sets MaxBytes, so its behavior is unchanged.
        var cap = request.MaxBytes > 0 ? request.MaxBytes : _settings.MaxFileTransferBytes;
        if (bytes.LongLength > cap)
            return new FileOpResponse(request.RequestId, false,
                $"Content of {bytes.LongLength} bytes exceeds the {cap}-byte limit.");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (request.Append)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
            stream.Write(bytes);
        }
        else
        {
            File.WriteAllBytes(path, bytes);
        }

        return new FileOpResponse(request.RequestId, true, null, TotalSize: bytes.LongLength);
    }

    private FileOpResponse Edit(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        if (!File.Exists(path))
            return new FileOpResponse(request.RequestId, false, $"File not found: {request.Path}");

        if (string.IsNullOrEmpty(request.OldText))
            return new FileOpResponse(request.RequestId, false, "Edit requires old_text.");

        var original = File.ReadAllText(path);
        var count = 0;
        var newText = request.NewText ?? "";

        var index = 0;
        var builder = new StringBuilder();
        while (true)
        {
            var found = original.IndexOf(request.OldText, index, StringComparison.Ordinal);
            if (found < 0)
            {
                builder.Append(original, index, original.Length - index);
                break;
            }

            builder.Append(original, index, found - index);
            builder.Append(newText);
            index = found + request.OldText.Length;
            count++;
        }

        if (count > 0)
            File.WriteAllText(path, builder.ToString());

        return new FileOpResponse(request.RequestId, true, null, ReplacementCount: count);
    }

    private FileOpResponse List(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        if (!Directory.Exists(path))
            return new FileOpResponse(request.RequestId, false, $"Directory not found: {request.Path}");

        var entries = new List<FileEntry>();
        var truncated = false;

        void Walk(string directory)
        {
            if (truncated)
                return;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).OrderBy(e => e, StringComparer.Ordinal);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children)
            {
                if (entries.Count >= _settings.MaxListEntries)
                {
                    truncated = true;
                    return;
                }

                var isDirectory = Directory.Exists(child);
                var name = Path.GetFileName(child);
                if (isDirectory && IsIgnoredDirectory(name))
                    continue;

                var info = isDirectory ? null : new FileInfo(child);
                entries.Add(new FileEntry(
                    resolver.ToSandboxRelative(ownerId, child),
                    isDirectory,
                    info?.Length ?? 0,
                    (isDirectory ? Directory.GetLastWriteTimeUtc(child) : info!.LastWriteTimeUtc)));

                if (isDirectory && request.Recursive)
                    Walk(child);
            }
        }

        Walk(path);

        return new FileOpResponse(request.RequestId, true, null, Entries: entries, Truncated: truncated);
    }

    private FileOpResponse Search(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        if (string.IsNullOrEmpty(request.Pattern))
            return new FileOpResponse(request.RequestId, false, "Search requires a pattern.");

        Regex regex;
        try
        {
            regex = new Regex(request.Pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return new FileOpResponse(request.RequestId, false, $"Invalid regex: {ex.Message}");
        }

        var hits = new List<SearchHit>();
        var truncated = false;

        void Walk(string directory)
        {
            if (truncated)
                return;

            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var child in children.OrderBy(c => c, StringComparer.Ordinal))
            {
                if (hits.Count >= _settings.MaxSearchHits)
                {
                    truncated = true;
                    return;
                }

                if (Directory.Exists(child))
                {
                    if (!IsIgnoredDirectory(Path.GetFileName(child)))
                        Walk(child);
                    continue;
                }

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(child);
                }
                catch (IOException)
                {
                    continue; // binary/locked/unreadable — skip rather than fail the whole search
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                for (var i = 0; i < lines.Length; i++)
                {
                    if (hits.Count >= _settings.MaxSearchHits)
                    {
                        truncated = true;
                        break;
                    }

                    if (regex.IsMatch(lines[i]))
                        hits.Add(new SearchHit(resolver.ToSandboxRelative(ownerId, child), i + 1, lines[i]));
                }
            }
        }

        if (Directory.Exists(path))
            Walk(path);
        else if (File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length && hits.Count < _settings.MaxSearchHits; i++)
            {
                if (regex.IsMatch(lines[i]))
                    hits.Add(new SearchHit(resolver.ToSandboxRelative(ownerId, path), i + 1, lines[i]));
            }
        }
        else
        {
            return new FileOpResponse(request.RequestId, false, $"Path not found: {request.Path}");
        }

        return new FileOpResponse(request.RequestId, true, null, Hits: hits, Truncated: truncated);
    }

    private FileOpResponse Delete(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        if (File.Exists(path))
        {
            File.Delete(path);
            return new FileOpResponse(request.RequestId, true, null);
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, request.Recursive);
            return new FileOpResponse(request.RequestId, true, null);
        }

        return new FileOpResponse(request.RequestId, false, $"Path not found: {request.Path}");
    }

    private FileOpResponse Move(Guid ownerId, FileOpRequest request)
    {
        var source = resolver.Resolve(ownerId, request.Path);
        if (string.IsNullOrEmpty(request.DestinationPath))
            return new FileOpResponse(request.RequestId, false, "Move requires a destination.");

        var destination = resolver.Resolve(ownerId, request.DestinationPath);

        if (File.Exists(source))
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.Move(source, destination, overwrite: true);
            return new FileOpResponse(request.RequestId, true, null);
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
            return new FileOpResponse(request.RequestId, true, null);
        }

        return new FileOpResponse(request.RequestId, false, $"Path not found: {request.Path}");
    }

    private FileOpResponse MakeDirectory(Guid ownerId, FileOpRequest request)
    {
        var path = resolver.Resolve(ownerId, request.Path);
        Directory.CreateDirectory(path);
        return new FileOpResponse(request.RequestId, true, null);
    }

    /// <summary>Builds a zip of either SelectedPaths (specific files/dirs) or everything under Path,
    /// respecting the same IgnoredDirectories rule List uses. Sums raw file sizes while walking and aborts
    /// BEFORE building the archive once the cap is exceeded, so a too-large selection fails fast rather
    /// than buffering a huge in-memory archive only to discard it. Tracks full paths already added
    /// (seenFullPaths) so an overlapping selection — e.g. SelectedPaths containing both a directory and a
    /// file already inside it — doesn't double-count bytes toward the cap or write a duplicate zip entry.</summary>
    private FileOpResponse Zip(Guid ownerId, FileOpRequest request)
    {
        var files = new List<(string FullPath, string EntryName)>();
        var seenFullPaths = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var cap = _settings.MaxZipBytes;

        if (request.SelectedPaths is { Count: > 0 } selected)
        {
            foreach (var relativePath in selected)
            {
                var full = resolver.Resolve(ownerId, relativePath);
                if (File.Exists(full))
                {
                    if (!seenFullPaths.Add(full))
                        continue;
                    totalBytes += new FileInfo(full).Length;
                    if (totalBytes > cap)
                        return TooLargeResponse(request, cap);
                    files.Add((full, resolver.ToSandboxRelative(ownerId, full)));
                }
                else if (Directory.Exists(full))
                {
                    if (!WalkForZip(full, ownerId, files, seenFullPaths, ref totalBytes, cap))
                        return TooLargeResponse(request, cap);
                }
                else
                {
                    return new FileOpResponse(request.RequestId, false, $"Path not found: {relativePath}");
                }
            }
        }
        else
        {
            var root = resolver.Resolve(ownerId, request.Path);
            if (!Directory.Exists(root))
                return new FileOpResponse(request.RequestId, false, $"Directory not found: {request.Path}");
            if (!WalkForZip(root, ownerId, files, seenFullPaths, ref totalBytes, cap))
                return TooLargeResponse(request, cap);
        }

        if (files.Count == 0)
            return new FileOpResponse(request.RequestId, false, "Nothing to zip — the selection is empty.");

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (fullPath, entryName) in files)
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(fullPath);
                fileStream.CopyTo(entryStream);
            }
        }

        var zipBytes = ms.ToArray();
        return new FileOpResponse(request.RequestId, true, null,
            ContentBase64: Convert.ToBase64String(zipBytes), TotalSize: zipBytes.LongLength);
    }

    private bool WalkForZip(string directory, Guid ownerId, List<(string FullPath, string EntryName)> files, HashSet<string> seenFullPaths, ref long totalBytes, long cap)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory).OrderBy(e => e, StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        foreach (var child in children)
        {
            if (Directory.Exists(child))
            {
                if (IsIgnoredDirectory(Path.GetFileName(child)))
                    continue;
                if (!WalkForZip(child, ownerId, files, seenFullPaths, ref totalBytes, cap))
                    return false;
            }
            else
            {
                if (!seenFullPaths.Add(child))
                    continue;
                totalBytes += new FileInfo(child).Length;
                if (totalBytes > cap)
                    return false;
                files.Add((child, resolver.ToSandboxRelative(ownerId, child)));
            }
        }

        return true;
    }

    private static FileOpResponse TooLargeResponse(FileOpRequest request, long cap) =>
        new(request.RequestId, false,
            $"Selection exceeds the {cap}-byte uncompressed zip limit — narrow the selection.");

    private static bool IsIgnoredDirectory(string directoryName) =>
        IgnoredDirectories.Contains(directoryName, StringComparer.OrdinalIgnoreCase);
}
