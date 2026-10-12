using System.Text.Json;

namespace PersonalAI.Core.Tools;

public sealed class ReadFileTool(Workspace workspace) : ITool
{
    private const int MaxChars = 200_000;

    public string Name => "read_file";

    public string Description =>
        "Read the contents of a text file. Call this before editing a file. " +
        "Use offset/limit (line numbers) to read a slice of a large file.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path, relative to the workspace root (or absolute)" },
            "offset": { "type": "integer", "description": "1-based line number to start reading from" },
            "limit": { "type": "integer", "description": "Maximum number of lines to read" }
          },
          "required": ["path"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path") ?? throw new ArgumentException("'path' is required.");
        var resolution = workspace.Resolve(path);
        if (resolution.LooksLikeSandboxPath)
            return new ToolCompleted(Workspace.SandboxPathHint(path), IsError: true);
        if (!resolution.IsInsideWorkspace && !context.PreApproved)
            return new ToolApprovalRequired($"read_file wants to read a path outside the workspace: {resolution.FullPath}", resolution.FullPath);

        var full = resolution.FullPath;
        if (!File.Exists(full))
            return new ToolCompleted($"File not found: {path}", IsError: true);

        var lines = await File.ReadAllLinesAsync(full, ct);
        var offset = Math.Max(1, arguments.GetIntArg("offset") ?? 1);
        var limit = arguments.GetIntArg("limit") ?? lines.Length;
        var slice = lines.Skip(offset - 1).Take(Math.Max(0, limit)).ToArray();

        var content = string.Join('\n', slice);
        var truncated = false;
        if (content.Length > MaxChars)
        {
            content = content[..MaxChars];
            truncated = true;
        }

        var header = $"[{path} — {lines.Length} lines total, showing lines {offset}-{offset + slice.Length - 1}]";
        return new ToolCompleted(header + "\n" + content + (truncated ? "\n[... output truncated, use offset/limit to read more]" : ""));
    }
}
