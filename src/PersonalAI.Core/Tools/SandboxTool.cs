using System.Text;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Sandbox.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Tools;

/// <summary>Gives the model a Docker container to run commands and manage files in: one per chat session, or one per
/// skill or agent run. Everything goes through the sandbox host broker (ISandboxHostClient); this tool never touches
/// Docker or the file system itself. SandboxProvisioningService is scoped, so it is resolved per call from a scope.</summary>
public sealed class SandboxTool(
    ISandboxHostClient hostClient,
    IOptions<SandboxSettings> settings,
    ToolConfigCache configCache,
    IServiceScopeFactory scopeFactory,
    ISandboxHostPowerSignaler powerSignaler,
    TimeProvider timeProvider) : ITool
{
    private static readonly HashSet<string> ValidActions = new(StringComparer.Ordinal)
    {
        "exec", "read_file", "write_file", "edit_file", "list_files", "search_files", "delete", "move", "mkdir", "ports"
    };

    public string Name => "sandbox";

    public string Description =>
        "Run commands and manage files inside a full Docker container — one per chat session (state " +
        "survives across turns) or one per skill/agent run (torn down when it ends). Use this for anything " +
        "the in-process file tools can't do: installing packages, running scripts/tests, cloning " +
        "repositories, or working with binary tools. Container networking is enabled. " +
        "IMPORTANT: each exec runs in a brand-new shell — 'cd' does NOT carry over to the next call. " +
        "Pass working_dir, or chain steps with && inside one call. Installed packages and files under " +
        "/work DO persist across calls and across turns. To run a server that must be reachable from " +
        "outside the sandbox, call action=\"ports\" first to get the small set of ports published for " +
        "this sandbox, then bind your server to 0.0.0.0:<one of those ports> inside the container — a " +
        "port not in that list will run fine but won't be reachable from outside. If a result starts " +
        "with '[Sandbox note]', the container was just silently recreated because the previous one was " +
        "gone (e.g. it idled out) — do not tell the user a process you started earlier is still running; " +
        "restart whatever you need, and treat the ports listed in that note (or a fresh \"ports\" call) " +
        "as authoritative over anything you remember from earlier in the conversation. A command that " +
        "looks like a package install (pip/npm/apt/...) or a broadly destructive operation (sudo, rm -rf, " +
        "a curl-pipe-to-shell, ...) will pause for the user's explicit approval before it runs.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["exec", "read_file", "write_file", "edit_file", "list_files", "search_files", "delete", "move", "mkdir", "ports"],
              "description": "'ports' takes no other arguments and returns the ports published for this sandbox — call it before starting a server you need reachable from outside the container. Ports can change if the sandbox had to be recreated — always trust this call's result over a port number remembered from earlier in the conversation."
            },
            "command": {
              "type": "array",
              "items": { "type": "string" },
              "description": "For exec: argv, e.g. [\"python3\",\"script.py\"]. Use [\"/bin/sh\",\"-c\",\"...\"] for pipes, globs or &&. Each exec is a FRESH shell — `cd` does NOT persist between calls; pass working_dir or chain with && inside one call. Installed packages and files under /work DO persist."
            },
            "working_dir": { "type": "string", "description": "For exec: directory, relative to /work (default /work)." },
            "timeout_seconds": { "type": "integer", "description": "For exec: max seconds to wait." },
            "path": { "type": "string", "description": "Path relative to /work. Required for every action except exec." },
            "destination": { "type": "string", "description": "For move: the new path." },
            "content": { "type": "string", "description": "For write_file: the file contents." },
            "append": { "type": "boolean", "description": "For write_file: append instead of overwrite." },
            "old_text": { "type": "string", "description": "For edit_file: exact text to replace." },
            "new_text": { "type": "string", "description": "For edit_file: replacement text." },
            "pattern": { "type": "string", "description": "For search_files: regex matched against file contents." },
            "recursive": { "type": "boolean", "description": "For list_files and delete." },
            "offset": { "type": "integer", "description": "For read_file: first line to return." },
            "limit": { "type": "integer", "description": "For read_file: max lines to return." }
          },
          "required": ["action"]
        }
        """;

    public IReadOnlyList<ToolSettingField> SettingsFields =>
    [
        new("DefaultImage", "Default sandbox image", ToolSettingFieldType.Text, Required: false),
        // Added to Sandbox:AllowedImages without a restart.
        new("AdditionalAllowedImages", "Additional allowed images (comma-separated)", ToolSettingFieldType.Text, Required: false),
        // Webhooks that wake and sleep the Docker host's machine (Wake-on-LAN relay, smart plug, Home Assistant...). Start is
        // called when no host is connected, Stop by the reaper once no sandbox is active.
        new("HostStartUrl", "Host start URL", ToolSettingFieldType.Url, Required: false),
        new("HostStopUrl", "Host stop URL", ToolSettingFieldType.Url, Required: false),
        // Overrides Sandbox:IdleTimeoutMinutes without a restart.
        new("IdleTimeoutMinutes", "Idle shutdown (minutes)", ToolSettingFieldType.Number, Required: false,
            Placeholder: $"Default: {settings.Value.IdleTimeoutMinutes}",
            Help: $"Stops a chat's sandbox container after this many minutes without activity; its files are kept. " +
                  $"Applies without a restart. Leave blank to use the server default ({settings.Value.IdleTimeoutMinutes})."),
        new("HostStatus", "Sandbox host", ToolSettingFieldType.Custom, Required: false, CustomComponentKey: "sandboxHostStatus")
    ];

    public bool IsConnected(ToolConfigCache cache)
    {
        return hostClient.IsConnected;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var action = arguments.GetStringArg("action");
        if (string.IsNullOrEmpty(action) || !ValidActions.Contains(action))
            return new ToolCompleted($"'action' must be one of: {string.Join(", ", ValidActions)}.", IsError: true);

        if (context.Sandbox is not { } owner)
            return new ToolCompleted(
                "The sandbox tool is not available in this context — no owning session or run was provided.", IsError: true);

        if (!hostClient.IsConnected && await WakeHostAsync(context, ct) is { } hostError)
            return new ToolCompleted(hostError, IsError: true);

        var (image, imageError) = ResolveImage();
        if (imageError is not null)
            return new ToolCompleted(imageError, IsError: true);

        await using var scope = scopeFactory.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<SandboxProvisioningService>();
        var (sandboxId, ports, wasRecreated, sandboxError) = await provisioning.EnsureAsync(owner, image!, ct);
        if (sandboxError is not null)
            return new ToolCompleted(sandboxError, IsError: true);

        var sandbox = new Sandbox(hostClient, provisioning, owner, sandboxId);
        ToolExecutionResult result;
        try
        {
            result = action switch
            {
                "exec" => await ExecAsync(sandbox, arguments, context, ct),
                "read_file" => await ReadFileAsync(sandbox, arguments, ct),
                "write_file" => await WriteFileAsync(sandbox, arguments, ct),
                "edit_file" => await EditFileAsync(sandbox, arguments, ct),
                "list_files" => await ListFilesAsync(sandbox, arguments, ct),
                "search_files" => await SearchFilesAsync(sandbox, arguments, ct),
                "delete" => await DeleteAsync(sandbox, arguments, ct),
                "move" => await MoveAsync(sandbox, arguments, ct),
                "mkdir" => await MkdirAsync(sandbox, arguments, ct),
                _ => RenderPorts(ports)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (SandboxHostDisconnectedException ex)
        {
            // The command may still have completed in the container even though the answer never came back.
            return new ToolCompleted(
                $"Sandbox host disconnected mid-command; its actual exit status is unknown. {ex.Message}", IsError: true);
        }
        catch (Exception ex)
        {
            return new ToolCompleted($"Sandbox tool failed: {ex.Message}", IsError: true);
        }

        // A recreated container is empty and has new ports: the model must hear that, even when this action failed.
        // Never on an approval request, whose text the approval flow shows as is.
        if ((wasRecreated || sandbox.RecreatedPorts is not null) && result is ToolCompleted completed)
            return completed with { Content = RecreationNotice(sandbox.RecreatedPorts ?? ports) + completed.Content };
        return result;
    }

    /// <summary>The Docker host is not connected. If a HostStartUrl is configured, sends the start signal and waits for the
    /// host to connect, signalling again now and then. Returns an error for the model, or null once it is connected.</summary>
    private async Task<string?> WakeHostAsync(ToolExecutionContext context, CancellationToken ct)
    {
        if (await powerSignaler.TryFireStartAsync(force: false, ct) is null)
            return "The sandbox host is not connected. No broker is currently reachable — try again later.";

        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.HostStartPollIntervalSeconds));
        var maxWait = TimeSpan.FromSeconds(Math.Max(0, settings.Value.HostStartMaxWaitSeconds));
        var retryInterval = TimeSpan.FromSeconds(Math.Max(1, settings.Value.HostStartRetryIntervalSeconds));

        if (maxWait > TimeSpan.Zero)
        {
            context.OnProgress?.Invoke("Waiting for sandbox host to boot...");
            var deadline = timeProvider.GetUtcNow() + maxWait;
            var nextRetry = timeProvider.GetUtcNow() + retryInterval;
            while (!hostClient.IsConnected && timeProvider.GetUtcNow() < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (timeProvider.GetUtcNow() >= nextRetry)
                {
                    await powerSignaler.TryFireStartAsync(force: true, ct);
                    nextRetry = timeProvider.GetUtcNow() + retryInterval;
                }
                await Task.Delay(pollInterval, timeProvider, ct);
            }
        }

        return hostClient.IsConnected
            ? null
            : $"A start signal was sent but the sandbox host did not connect within {(int)maxWait.TotalSeconds}s. " +
              "The host may still be booting — try again shortly.";
    }

    /// <summary>One call's sandbox. When the host reports the container gone (it never started the command), it is
    /// recreated with its ports and the request is retried exactly once; RecreatedPorts then holds the new ports.</summary>
    private sealed class Sandbox(ISandboxHostClient hostClient, SandboxProvisioningService provisioning, SandboxOwner owner, Guid id)
    {
        public Guid Id => id;
        public IReadOnlyList<int>? RecreatedPorts { get; private set; }

        public async Task<SandboxExecResult> ExecAsync(IReadOnlyList<string> command, string? workingDir, TimeSpan timeout, CancellationToken ct)
        {
            var result = await hostClient.ExecAsync(id, command, workingDir, timeout, ct);
            if (!result.ContainerMissing) return result;

            var recreateError = await RecreateAsync(ct);
            if (recreateError is not null)
                return result with { Error = $"{result.Error} (automatic recreation also failed: {recreateError})" };

            var retry = await hostClient.ExecAsync(id, command, workingDir, timeout, ct);
            return retry.ContainerMissing
                ? retry with { Error = $"{retry.Error} (still reported missing after one automatic recreation; not retrying again)" }
                : retry;
        }

        public async Task<FileOpResponse> FileOpAsync(FileOpRequest request, CancellationToken ct)
        {
            var response = await hostClient.FileOpAsync(request, ct);
            if (!response.ContainerMissing) return response;

            var recreateError = await RecreateAsync(ct);
            if (recreateError is not null)
                return response with { Error = $"{response.Error} (automatic recreation also failed: {recreateError})" };

            // A new RequestId: requests in flight are matched to their answers by it.
            var retry = await hostClient.FileOpAsync(request with { RequestId = Guid.NewGuid() }, ct);
            return retry.ContainerMissing
                ? retry with { Error = $"{retry.Error} (still reported missing after one automatic recreation; not retrying again)" }
                : retry;
        }

        private async Task<string?> RecreateAsync(CancellationToken ct)
        {
            var (recreated, ports, error) = await provisioning.RecreateAfterContainerMissingAsync(owner, ct);
            if (recreated) RecreatedPorts = ports;
            return recreated ? null : error;
        }
    }

    private ToolExecutionResult RenderPorts(IReadOnlyList<int> ports)
    {
        if (ports.Count == 0)
            return new ToolCompleted("No ports are published for this sandbox.", IsError: false);

        // The host's address as seen by the API is where a published port is reachable from.
        if (hostClient.RemoteIp is not { } remoteIp)
            return new ToolCompleted(
                $"Ports published for this sandbox: {string.Join(", ", ports)}. " +
                "Bind a server to 0.0.0.0:<one of these ports> inside the container to make it reachable " +
                "from outside — a port not in this list will run but won't be reachable.",
                IsError: false);

        var host = remoteIp.Contains(':') ? $"[{remoteIp}]" : remoteIp; // IPv6 is bracketed in a URL
        var urls = string.Join(", ", ports.Select(p => $"http://{host}:{p}"));
        return new ToolCompleted(
            $"Ports published for this sandbox: {string.Join(", ", ports)}. Bind a server to " +
            $"0.0.0.0:<one of these ports> inside the container and it will be reachable at {urls} " +
            "— a port not in this list will run but won't be reachable.",
            IsError: false);
    }

    /// <summary>Otherwise the model can't tell "same container as last turn" from "a new, empty one" and reports
    /// continuity that isn't real.</summary>
    private static string RecreationNotice(IReadOnlyList<int> ports)
    {
        var portsText = ports.Count == 0 ? "none" : string.Join(", ", ports);
        return "[Sandbox note] This sandbox's previous container was gone, so a fresh one was just " +
               "created for this call. Any server or background process started in an earlier turn is " +
               "NOT running anymore — restart it if it's still needed. Currently published ports: " +
               $"{portsText}. Use these, not any ports or URLs you recall from earlier in the " +
               "conversation.\n\n";
    }

    /// <summary>The image is operator configuration (the tool's DefaultImage setting, else Sandbox:DefaultImage), never a
    /// model argument, so the allowed list can't be bypassed by asking for another image.</summary>
    private (string? Image, string? Error) ResolveImage()
    {
        var image = configCache.GetValue("sandbox", "DefaultImage");
        if (string.IsNullOrWhiteSpace(image)) image = settings.Value.DefaultImage;

        // Sandbox:AllowedImages (needs a restart) plus the live "Additional allowed images" setting. Empty means any image.
        var allowed = new List<string>(settings.Value.AllowedImages);
        var additional = configCache.GetValue("sandbox", "AdditionalAllowedImages") ?? "";
        foreach (var entry in additional.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (!allowed.Contains(entry, StringComparer.Ordinal)) allowed.Add(entry);

        if (allowed.Count > 0 && !allowed.Contains(image!))
            return (null, $"Configured sandbox image '{image}' is not in the allowed list ({string.Join(", ", allowed)}). " +
                          "Fix Sandbox:AllowedImages, the sandbox tool's Additional allowed images setting, or its Default sandbox image setting.");

        return (image, null);
    }

    private async Task<ToolExecutionResult> ExecAsync(Sandbox sandbox, JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var command = arguments.GetStringArrayArg("command");
        if (command is null || command.Count == 0)
            return new ToolCompleted("'command' (a non-empty array of argv strings) is required for exec.", IsError: true);

        // Same pause as a file tool's workspace escape: chat asks the user; skill and agent runs refuse it.
        var joined = string.Join(' ', command);
        if (!context.PreApproved && settings.Value.RequireApprovalForDangerousCommands && IsDangerous(joined))
            return new ToolApprovalRequired(
                "This command matches a pattern flagged as a package install or a broadly destructive " +
                "operation, so it needs your explicit approval before it runs in the sandbox.",
                joined);

        var timeoutSeconds = Math.Clamp(
            arguments.GetIntArg("timeout_seconds") ?? settings.Value.DefaultExecTimeoutSeconds,
            1, Math.Max(1, settings.Value.MaxExecTimeoutSeconds));
        var result = await sandbox.ExecAsync(command, arguments.GetStringArg("working_dir"), TimeSpan.FromSeconds(timeoutSeconds), ct);

        // No exit code and no timeout: the command never ran (a broker or Docker failure), not an empty success.
        if (result.ExitCode is null && !result.TimedOut)
            return new ToolCompleted(
                $"The sandbox command failed to run: {result.Error ?? "no diagnostic information was returned."}",
                IsError: true);

        // A non-zero exit is not a tool failure: the command ran, and the model sees the exit code.
        return new ToolCompleted(RenderExec(result), IsError: false);
    }

    /// <summary>True when the command matches one of Sandbox:DangerousCommandPatterns. A malformed pattern is skipped, so
    /// one typo doesn't take down the whole check.</summary>
    private bool IsDangerous(string joinedCommand)
    {
        foreach (var pattern in settings.Value.DangerousCommandPatterns)
        {
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(joinedCommand, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;
            }
            catch (ArgumentException)
            {
            }
        }
        return false;
    }

    /// <summary>Exit code and timeout notice first: history trimming cuts a tool result from the end.</summary>
    private string RenderExec(SandboxExecResult result)
    {
        var maxChars = Math.Max(0, settings.Value.MaxOutputCharsPerStream);
        var sb = new StringBuilder();
        sb.Append("Exit code: ").Append(result.ExitCode?.ToString() ?? "unknown");
        if (result.TimedOut)
            sb.Append(" (command timed out and was terminated; output below is incomplete)");
        sb.Append('\n');
        sb.Append("--- stdout ---\n").Append(RenderStream(result.Stdout, result.StdoutTruncated, maxChars)).Append('\n');
        sb.Append("--- stderr ---\n").Append(RenderStream(result.Stderr, result.StderrTruncated, maxChars));
        return sb.ToString();
    }

    /// <summary>Keeps the end of the output, where a shell's error usually is.</summary>
    private static string RenderStream(string text, bool truncatedUpstream, int maxChars)
    {
        var truncated = truncatedUpstream;
        if (text.Length > maxChars)
        {
            text = text[^maxChars..];
            truncated = true;
        }
        return truncated ? $"[...output truncated...]\n{text}" : text;
    }

    private async Task<ToolExecutionResult> ReadFileAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for read_file.", IsError: true);

        var offset = Math.Max(0, arguments.GetIntArg("offset") ?? 0);
        var limit = Math.Max(0, arguments.GetIntArg("limit") ?? 0);
        var response = await sandbox.FileOpAsync(new FileOpRequest(
            Guid.NewGuid(), sandbox.Id, FileOp.Read, path, Offset: offset, Limit: limit, MaxBytes: settings.Value.MaxFileTransferBytes), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to read {path}.", IsError: true);

        var content = string.IsNullOrEmpty(response.ContentBase64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(response.ContentBase64));
        var note = response.Truncated ? "\n[... output truncated, use offset/limit to read more]" : "";
        return new ToolCompleted($"[{path}]\n{content}{note}");
    }

    private async Task<ToolExecutionResult> WriteFileAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for write_file.", IsError: true);

        var content = arguments.GetStringArg("content");
        if (content is null)
            return new ToolCompleted("'content' is required for write_file.", IsError: true);

        var append = GetBoolArg(arguments, "append") ?? false;
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.LongLength > settings.Value.MaxFileTransferBytes)
            return new ToolCompleted(
                $"'content' is {bytes.LongLength} bytes, exceeding the configured limit of {settings.Value.MaxFileTransferBytes} bytes.", IsError: true);

        var response = await sandbox.FileOpAsync(new FileOpRequest(
            Guid.NewGuid(), sandbox.Id, FileOp.Write, path, ContentBase64: Convert.ToBase64String(bytes), Append: append), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to write {path}.", IsError: true);

        return new ToolCompleted($"{(append ? "Appended" : "Wrote")} {bytes.Length} bytes to {path}.");
    }

    private async Task<ToolExecutionResult> EditFileAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for edit_file.", IsError: true);

        var oldText = arguments.GetStringArg("old_text");
        if (string.IsNullOrEmpty(oldText))
            return new ToolCompleted("'old_text' is required for edit_file and must not be empty.", IsError: true);

        var newText = arguments.GetStringArg("new_text");
        if (newText is null)
            return new ToolCompleted("'new_text' is required for edit_file.", IsError: true);

        var response = await sandbox.FileOpAsync(new FileOpRequest(
            Guid.NewGuid(), sandbox.Id, FileOp.Edit, path, OldText: oldText, NewText: newText), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to edit {path}.", IsError: true);

        // The count, not the content, like EditFileTool: the model can tell a no-op (0) without re-reading the file.
        return new ToolCompleted($"Replaced {response.ReplacementCount} occurrence(s) in {path}.");
    }

    private async Task<ToolExecutionResult> ListFilesAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path)) path = ".";
        var recursive = GetBoolArg(arguments, "recursive") ?? false;

        var response = await sandbox.FileOpAsync(new FileOpRequest(Guid.NewGuid(), sandbox.Id, FileOp.List, path, Recursive: recursive), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to list {path}.", IsError: true);

        var entries = response.Entries ?? [];
        if (entries.Count == 0)
            return new ToolCompleted("(empty directory)");

        var sb = new StringBuilder();
        foreach (var entry in entries)
            sb.AppendLine(entry.IsDirectory ? $"{entry.Path}/" : $"{entry.Path} ({entry.Size} bytes)");
        if (response.Truncated)
            sb.AppendLine($"[... truncated at {settings.Value.MaxListEntries} entries, list a subdirectory for more]");
        return new ToolCompleted(sb.ToString());
    }

    private async Task<ToolExecutionResult> SearchFilesAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var pattern = arguments.GetStringArg("pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return new ToolCompleted("'pattern' is required for search_files.", IsError: true);

        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path)) path = ".";

        var response = await sandbox.FileOpAsync(new FileOpRequest(Guid.NewGuid(), sandbox.Id, FileOp.Search, path, Pattern: pattern), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? "Search failed.", IsError: true);

        var hits = response.Hits ?? [];
        if (hits.Count == 0)
            return new ToolCompleted("No matches found.");

        var sb = new StringBuilder();
        foreach (var hit in hits)
            sb.AppendLine($"{hit.Path}:{hit.LineNumber}: {hit.Line}");
        if (response.Truncated)
            sb.AppendLine($"[... truncated at {settings.Value.MaxSearchHits} matches, narrow the search]");
        return new ToolCompleted(sb.ToString());
    }

    private async Task<ToolExecutionResult> DeleteAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for delete.", IsError: true);

        var recursive = GetBoolArg(arguments, "recursive") ?? false;
        var response = await sandbox.FileOpAsync(new FileOpRequest(Guid.NewGuid(), sandbox.Id, FileOp.Delete, path, Recursive: recursive), ct);
        return response.Success
            ? new ToolCompleted($"Deleted {path}.")
            : new ToolCompleted(response.Error ?? $"Failed to delete {path}.", IsError: true);
    }

    private async Task<ToolExecutionResult> MoveAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for move.", IsError: true);

        var destination = arguments.GetStringArg("destination");
        if (string.IsNullOrWhiteSpace(destination))
            return new ToolCompleted("'destination' is required for move.", IsError: true);

        var response = await sandbox.FileOpAsync(new FileOpRequest(Guid.NewGuid(), sandbox.Id, FileOp.Move, path, DestinationPath: destination), ct);
        return response.Success
            ? new ToolCompleted($"Moved {path} to {destination}.")
            : new ToolCompleted(response.Error ?? $"Failed to move {path}.", IsError: true);
    }

    private async Task<ToolExecutionResult> MkdirAsync(Sandbox sandbox, JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for mkdir.", IsError: true);

        var response = await sandbox.FileOpAsync(new FileOpRequest(Guid.NewGuid(), sandbox.Id, FileOp.MakeDirectory, path), ct);
        return response.Success
            ? new ToolCompleted($"Created directory {path}.")
            : new ToolCompleted(response.Error ?? $"Failed to create directory {path}.", IsError: true);
    }

    private static bool? GetBoolArg(JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;
    }
}
