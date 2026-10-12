using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalAI.Core.Tools;

public sealed class SearchFilesTool(Workspace workspace) : ITool
{
    private const int MaxMatches = 200;
    private const long MaxFileBytes = 1_000_000;

    public string Name => "search_files";

    public string Description =>
        "Search file contents with a regular expression. Returns matching lines as 'path:line: text'. " +
        "Optionally filter by a file name wildcard like *.cs.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regular expression to search for (case-insensitive)" },
            "path": { "type": "string", "description": "Directory to search in, relative to the workspace root (or absolute). Defaults to the root." },
            "file_pattern": { "type": "string", "description": "File name wildcard filter, e.g. *.cs or *.json" }
          },
          "required": ["pattern"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var pattern = arguments.GetStringArg("pattern") ?? throw new ArgumentException("'pattern' is required.");
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            path = "."; // empty/missing path means the workspace root
        var filePattern = arguments.GetStringArg("file_pattern");
        if (string.IsNullOrWhiteSpace(filePattern))
            filePattern = "*";

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return new ToolCompleted($"Invalid regular expression: {ex.Message}", IsError: true);
        }

        var resolution = workspace.Resolve(path);
        if (resolution.LooksLikeSandboxPath)
            return new ToolCompleted(Workspace.SandboxPathHint(path), IsError: true);
        if (!resolution.IsInsideWorkspace && !context.PreApproved)
            return new ToolApprovalRequired($"search_files wants to search a path outside the workspace: {resolution.FullPath}", resolution.FullPath);

        var root = resolution.FullPath;
        if (!Directory.Exists(root))
            return new ToolCompleted($"Directory not found: {path}", IsError: true);

        var sb = new StringBuilder();
        var matches = 0;

        foreach (var file in EnumerateFiles(root, filePattern))
        {
            ct.ThrowIfCancellationRequested();
            if (matches >= MaxMatches) break;
            if (new FileInfo(file).Length > MaxFileBytes) continue;

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, ct);
            }
            catch (IOException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length && matches < MaxMatches; i++)
            {
                bool isMatch;
                try { isMatch = regex.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { break; }
                if (!isMatch) continue;

                var text = lines[i].Length > 300 ? lines[i][..300] + "..." : lines[i];
                sb.AppendLine($"{workspace.ToRelative(file)}:{i + 1}: {text.Trim()}");
                matches++;
            }
        }

        if (matches == 0)
            return new ToolCompleted("No matches found.");
        if (matches >= MaxMatches)
            sb.AppendLine($"[... truncated at {MaxMatches} matches, narrow the search]");
        return new ToolCompleted(sb.ToString());
    }

    private static IEnumerable<string> EnumerateFiles(string root, string filePattern)
    {
        foreach (var file in Directory.EnumerateFiles(root, filePattern))
            yield return file;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (Workspace.IsIgnoredDirectory(Path.GetFileName(dir)))
                continue;
            foreach (var file in EnumerateFiles(dir, filePattern))
                yield return file;
        }
    }
}
