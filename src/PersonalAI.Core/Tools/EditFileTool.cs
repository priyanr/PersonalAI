using System.Text.Json;

namespace PersonalAI.Core.Tools;

public sealed class EditFileTool(Workspace workspace) : ITool
{
    public string Name => "edit_file";

    public string Description =>
        "Replace an exact string in a file with a new string. old_string must appear exactly once " +
        "in the file — include enough surrounding context to make it unique. Read the file first.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File path, relative to the workspace root (or absolute)" },
            "old_string": { "type": "string", "description": "Exact text to replace (must match exactly once, including whitespace)" },
            "new_string": { "type": "string", "description": "Replacement text" }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path") ?? throw new ArgumentException("'path' is required.");
        var oldString = arguments.GetStringArg("old_string") ?? throw new ArgumentException("'old_string' is required.");
        var newString = arguments.GetStringArg("new_string") ?? throw new ArgumentException("'new_string' is required.");
        if (oldString.Length == 0)
            return new ToolCompleted("'old_string' must not be empty. To create a new file or fully replace a file's content, use write_file instead.", IsError: true);

        var resolution = workspace.Resolve(path);
        if (resolution.LooksLikeSandboxPath)
            return new ToolCompleted(Workspace.SandboxPathHint(path), IsError: true);
        if (!resolution.IsInsideWorkspace && !context.PreApproved)
            return new ToolApprovalRequired($"edit_file wants to modify a path outside the workspace: {resolution.FullPath}", resolution.FullPath);

        var full = resolution.FullPath;
        if (!File.Exists(full))
            return new ToolCompleted($"File not found: {path}", IsError: true);

        var content = await File.ReadAllTextAsync(full, ct);
        var occurrences = CountOccurrences(content, oldString);
        if (occurrences == 0)
            return new ToolCompleted("old_string was not found in the file. Re-read the file and match the exact text, including whitespace.", IsError: true);
        if (occurrences > 1)
            return new ToolCompleted($"old_string matches {occurrences} places in the file. Include more surrounding context so it matches exactly once.", IsError: true);

        var index = content.IndexOf(oldString, StringComparison.Ordinal);
        var updated = content[..index] + newString + content[(index + oldString.Length)..];
        await File.WriteAllTextAsync(full, updated, ct);
        return new ToolCompleted($"Edited {path}: replaced {oldString.Length} chars with {newString.Length} chars.");
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
