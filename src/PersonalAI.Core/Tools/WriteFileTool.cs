using System.Text.Json;

namespace PersonalAI.Core.Tools;

public sealed class WriteFileTool(Workspace workspace) : ITool
{
    public string Name => "write_file";

    public string Description =>
        "Create a new file or completely overwrite an existing one with the given content. " +
        "Parent directories are created automatically. For small changes to an existing file prefer edit_file.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path, relative to the workspace root (or absolute)" },
            "content": { "type": "string", "description": "Full content to write to the file" }
          },
          "required": ["path", "content"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path") ?? throw new ArgumentException("'path' is required.");
        var content = arguments.GetStringArg("content") ?? throw new ArgumentException("'content' is required.");
        var resolution = workspace.Resolve(path);
        if (resolution.LooksLikeSandboxPath)
            return new ToolCompleted(Workspace.SandboxPathHint(path), IsError: true);
        if (!resolution.IsInsideWorkspace && !context.PreApproved)
            return new ToolApprovalRequired($"write_file wants to write outside the workspace: {resolution.FullPath}", resolution.FullPath);

        var full = resolution.FullPath;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var existed = File.Exists(full);
        await File.WriteAllTextAsync(full, content, ct);
        return new ToolCompleted($"{(existed ? "Overwrote" : "Created")} {path} ({content.Length} chars).");
    }
}
