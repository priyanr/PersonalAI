using System.Text;
using System.Text.Json;

namespace PersonalAI.Core.Tools;

public sealed class ListFilesTool(Workspace workspace) : ITool
{
    private const int MaxEntries = 500;

    public string Name => "list_files";

    public string Description =>
        "Recursively list files and directories under a path. Defaults to the workspace root. " +
        "Common build/VCS directories (.git, bin, obj, node_modules, ...) are skipped.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory to list, relative to the workspace root (or absolute). Defaults to the root." }
          },
          "required": []
        }
        """;

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            path = "."; // empty/missing path means the workspace root

        var resolution = workspace.Resolve(path);
        if (resolution.LooksLikeSandboxPath)
            return Task.FromResult<ToolExecutionResult>(new ToolCompleted(Workspace.SandboxPathHint(path), IsError: true));
        if (!resolution.IsInsideWorkspace && !context.PreApproved)
            return Task.FromResult<ToolExecutionResult>(
                new ToolApprovalRequired($"list_files wants to list a path outside the workspace: {resolution.FullPath}", resolution.FullPath));

        var full = resolution.FullPath;
        if (!Directory.Exists(full))
            return Task.FromResult<ToolExecutionResult>(new ToolCompleted($"Directory not found: {path}", IsError: true));

        var sb = new StringBuilder();
        var count = 0;
        var truncated = false;
        Walk(full, 0);

        if (count == 0)
            sb.AppendLine("(empty directory)");
        if (truncated)
            sb.AppendLine($"[... truncated at {MaxEntries} entries, list a subdirectory for more]");
        return Task.FromResult<ToolExecutionResult>(new ToolCompleted(sb.ToString()));

        void Walk(string directory, int depth)
        {
            if (truncated) return;
            var indent = new string(' ', depth * 2);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (count >= MaxEntries) { truncated = true; return; }
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (Workspace.IsIgnoredDirectory(name)) continue;
                    sb.AppendLine($"{indent}{name}/");
                    count++;
                    Walk(entry, depth + 1);
                }
                else
                {
                    sb.AppendLine($"{indent}{name}");
                    count++;
                }
            }
        }
    }
}
