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

/// <summary>Gives the model a full Docker container to run commands and manage files in — the same nine
/// operations the in-process file tools already offer (read/write/edit/list/search, plus delete/move/mkdir)
/// and one more (exec) that none of them can do at all. Everything is delegated to the sandbox host broker
/// over ISandboxHostClient; this tool never touches Docker or the filesystem directly.
///
/// Like GmailTool, this lives inside the singleton ToolRegistry but needs the Scoped
/// SandboxProvisioningService (it wraps the Scoped ISandboxRepository/AppDbContext) — captured via
/// IServiceScopeFactory rather than as a constructor dependency, the same captive-dependency workaround, so
/// a fresh scope is opened only for the get-or-create lookup/update.</summary>
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
        // Sandbox:AllowedImages (Sandbox:AllowedImages / Sandbox__AllowedImages__N) is operator config that
        // needs an app restart to change. This is the live-editable complement to it: a comma-separated
        // list stored in ToolConfig, picked up immediately (no restart) since ToolConfigCache is updated
        // synchronously by the PATCH /api/tools/sandbox request that sets it.
        new("AdditionalAllowedImages", "Additional allowed images (comma-separated)", ToolSettingFieldType.Text, Required: false),
        // Both optional and both plain webhook triggers -- whatever wakes/sleeps the machine that runs
        // the Docker host (a Wake-on-LAN relay, a smart plug's API, a Home Assistant/n8n webhook, ...).
        // Called by SandboxHostPowerSignaler: Start when the tool finds no broker connected, Stop by the
        // reaper once it observes zero active sandboxes remaining (never by the tool itself).
        new("HostStartUrl", "Host start URL", ToolSettingFieldType.Url, Required: false),
        new("HostStopUrl", "Host stop URL", ToolSettingFieldType.Url, Required: false),
        // Live override of Sandbox:IdleTimeoutMinutes, read by SandboxReaperHostedService on every tick —
        // blank falls back to the config value. Retires the chat container only; its files are kept.
        new("IdleTimeoutMinutes", "Idle shutdown (minutes)", ToolSettingFieldType.Number, Required: false,
            Placeholder: $"Default: {settings.Value.IdleTimeoutMinutes}",
            Help: $"Stops a chat's sandbox container after this many minutes without activity; its files are kept. " +
                  $"Applies without a restart. Leave blank to use the server default ({settings.Value.IdleTimeoutMinutes})."),
        new("HostStatus", "Sandbox host", ToolSettingFieldType.Custom, Required: false, CustomComponentKey: "sandboxHostStatus")
    ];

    public bool IsConnected(ToolConfigCache cache) => hostClient.IsConnected;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var action = arguments.GetStringArg("action");
        if (string.IsNullOrEmpty(action) || !ValidActions.Contains(action))
            return new ToolCompleted($"'action' must be one of: {string.Join(", ", ValidActions)}.", IsError: true);

        if (context.Sandbox is not { } owner)
            return new ToolCompleted(
                "The sandbox tool is not available in this context — no owning session or run was provided.", IsError: true);

        // Short-circuit rather than let EnsureSandboxAsync/ExecAsync time out against a dead connection.
        // If a HostStartUrl is configured, this is also the trigger to wake whatever machine runs the
        // Docker host — the broker obviously can't dial in while that machine itself is off.
        if (!hostClient.IsConnected)
        {
            var startResult = await powerSignaler.TryFireStartAsync(force: false, ct);

            // No HostStartUrl configured — nothing to wait for, fail immediately as before.
            if (startResult is null)
                return new ToolCompleted(
                    "The sandbox host is not connected. No broker is currently reachable — try again later.",
                    IsError: true);

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

            if (!hostClient.IsConnected)
                return new ToolCompleted(
                    $"A start signal was sent but the sandbox host did not connect within {(int)maxWait.TotalSeconds}s. " +
                    "The host may still be booting — try again shortly.",
                    IsError: true);
        }

        var (image, imageError) = ResolveImage();
        if (imageError is not null)
            return new ToolCompleted(imageError, IsError: true);

        await using var scope = scopeFactory.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<SandboxProvisioningService>();
        var (sandboxId, ports, wasRecreated, sandboxError) = await provisioning.EnsureAsync(owner, image!, ct);
        if (sandboxError is not null)
            return new ToolCompleted(sandboxError, IsError: true);

        var outcome = new SelfHealOutcome();
        try
        {
            var actionResult = action switch
            {
                "exec" => await ExecAsync(owner, sandboxId, provisioning, outcome, arguments, context, ct),
                "read_file" => await ReadFileAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "write_file" => await WriteFileAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "edit_file" => await EditFileAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "list_files" => await ListFilesAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "search_files" => await SearchFilesAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "delete" => await DeleteAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "move" => await MoveAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "mkdir" => await MkdirAsync(owner, sandboxId, provisioning, outcome, arguments, ct),
                "ports" => RenderPorts(ports),
                _ => new ToolCompleted($"Unsupported action '{action}'.", IsError: true)
            };

            // Only a plain ToolCompleted gets the notice prepended — never ToolApprovalRequired (exec's
            // dangerous-command pause has a fixed prompt string the approval flow matches on). No IsError
            // check: the freshness/port info is still useful context even when this particular action
            // failed. outcome.Recreated covers a mid-call self-heal (the container was gone even though
            // EnsureSandboxAsync above thought it was Running); wasRecreated covers the top-level
            // EnsureAsync recreation. Either can fire independently — only one notice is ever prepended.
            var wasRecreatedOverall = wasRecreated || outcome.Recreated;
            var noticePorts = outcome.Recreated ? outcome.Ports : ports;
            if (wasRecreatedOverall && actionResult is ToolCompleted completed)
                return completed with { Content = BuildRecreationNotice(noticePorts) + completed.Content };

            return actionResult;
        }
        catch (OperationCanceledException) { throw; }
        catch (SandboxHostDisconnectedException ex)
        {
            // The command's actual effect is unknown — it may have completed on the container even
            // though the broker connection died before the response made it back.
            return new ToolCompleted(
                $"Sandbox host disconnected mid-command; its actual exit status is unknown. {ex.Message}", IsError: true);
        }
        catch (Exception ex)
        {
            return new ToolCompleted($"Sandbox tool failed: {ex.Message}", IsError: true);
        }
    }

    /// <summary>Not static — needs hostClient.RemoteIp, the connected broker's own source address as the
    /// API's TCP stack (or a trusted proxy's X-Forwarded-For) sees it, which is where a published port is
    /// actually reachable from. Falls back to the old generic guidance if it's ever unset (defensive only
    /// — "ports" already requires IsConnected to reach here, so RemoteIp should always be populated by
    /// the time a real broker has completed its handshake).</summary>
    private ToolExecutionResult RenderPorts(IReadOnlyList<int> ports)
    {
        if (ports.Count == 0)
            return new ToolCompleted("No ports are published for this sandbox.", IsError: false);

        if (hostClient.RemoteIp is not { } remoteIp)
            return new ToolCompleted(
                $"Ports published for this sandbox: {string.Join(", ", ports)}. " +
                "Bind a server to 0.0.0.0:<one of these ports> inside the container to make it reachable " +
                "from outside — a port not in this list will run but won't be reachable.",
                IsError: false);

        // IPv6 needs bracketing in a URL ("http://[::1]:51234") -- an unbracketed IPv6 address's own
        // colons would be indistinguishable from the port separator.
        var host = remoteIp.Contains(':') ? $"[{remoteIp}]" : remoteIp;
        var urls = string.Join(", ", ports.Select(p => $"http://{host}:{p}"));
        return new ToolCompleted(
            $"Ports published for this sandbox: {string.Join(", ", ports)}. Bind a server to " +
            $"0.0.0.0:<one of these ports> inside the container and it will be reachable at {urls} " +
            "— a port not in this list will run but won't be reachable.",
            IsError: false);
    }

    /// <summary>Mutable out-parameter substitute for the 9 action handlers: threading a 3-tuple return
    /// through every one of them just to carry "did a mid-call self-heal fire, and with what ports" would
    /// touch every line that reads their result. Each handler mutates this instead when
    /// Exec/FileOpWithSelfHealAsync recreates the container mid-call.</summary>
    private sealed class SelfHealOutcome
    {
        public bool Recreated;
        public IReadOnlyList<int> Ports = [];
    }

    /// <summary>Wraps hostClient.ExecAsync: if the broker reports the container itself is gone
    /// (ContainerMissing — resolve failed, or Docker itself threw 404/409, so the command never actually
    /// started), marks the DB row Orphaned, recreates the container preserving its ports, and retries the
    /// exact same command once. Retries exactly once by construction — a still-ContainerMissing result
    /// after the retry is returned as-is with Error augmented, never retried again.</summary>
    private async Task<SandboxExecResult> ExecWithSelfHealAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        IReadOnlyList<string> command, string? workingDir, TimeSpan timeout, CancellationToken ct)
    {
        var result = await hostClient.ExecAsync(sandboxId, command, workingDir, timeout, ct);
        if (!result.ContainerMissing)
            return result;

        var (recreated, newPorts, recreateError) = await provisioning.RecreateAfterContainerMissingAsync(owner, ct);
        if (!recreated)
            return result with { Error = $"{result.Error} (automatic recreation also failed: {recreateError})" };

        var retry = await hostClient.ExecAsync(sandboxId, command, workingDir, timeout, ct);
        if (retry.ContainerMissing)
            return retry with { Error = $"{retry.Error} (still reported missing after one automatic recreation; not retrying again)" };

        outcome.Recreated = true;
        outcome.Ports = newPorts;
        return retry;
    }

    /// <summary>File-op equivalent of ExecWithSelfHealAsync. The retry MUST use a fresh RequestId — the
    /// pump correlates in-flight requests by it, so reusing the first attempt's id for a second call would
    /// be wrong even though the first attempt (ContainerMissing means resolve failed before the file
    /// service was ever reached) had no observable effect to worry about duplicating.</summary>
    private async Task<FileOpResponse> FileOpWithSelfHealAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        FileOpRequest request, CancellationToken ct)
    {
        var response = await hostClient.FileOpAsync(request, ct);
        if (!response.ContainerMissing)
            return response;

        var (recreated, newPorts, recreateError) = await provisioning.RecreateAfterContainerMissingAsync(owner, ct);
        if (!recreated)
            return response with { Error = $"{response.Error} (automatic recreation also failed: {recreateError})" };

        var retry = await hostClient.FileOpAsync(request with { RequestId = Guid.NewGuid() }, ct);
        if (retry.ContainerMissing)
            return retry with { Error = $"{retry.Error} (still reported missing after one automatic recreation; not retrying again)" };

        outcome.Recreated = true;
        outcome.Ports = newPorts;
        return retry;
    }

    /// <summary>Prepended to a ToolCompleted's Content whenever GetOrCreateSandboxAsync had to (re)create
    /// a container for an owner that had used one before — the model otherwise has no way to tell "same
    /// container as last turn" from "brand-new, empty container" and will confidently report continuity
    /// that isn't real.</summary>
    private static string BuildRecreationNotice(IReadOnlyList<int> ports)
    {
        var portsText = ports.Count == 0 ? "none" : string.Join(", ", ports);
        return "[Sandbox note] This sandbox's previous container was gone, so a fresh one was just " +
               "created for this call. Any server or background process started in an earlier turn is " +
               "NOT running anymore — restart it if it's still needed. Currently published ports: " +
               $"{portsText}. Use these, not any ports or URLs you recall from earlier in the " +
               "conversation.\n\n";
    }

    /// <summary>The image is operator configuration (the tool's own DefaultImage settings field, falling
    /// back to Sandbox:DefaultImage), never a per-call model argument — the allow-list is the boundary, so
    /// there is nothing for the model to bypass by asking for a different image.</summary>
    private (string? Image, string? Error) ResolveImage()
    {
        var image = configCache.GetValue("sandbox", "DefaultImage");
        if (string.IsNullOrWhiteSpace(image)) image = settings.Value.DefaultImage;

        var allowed = EffectiveAllowedImages();
        if (allowed.Count > 0 && !allowed.Contains(image!))
            return (null, $"Configured sandbox image '{image}' is not in the allowed list ({string.Join(", ", allowed)}). " +
                          "Fix Sandbox:AllowedImages, the sandbox tool's Additional allowed images setting, or its Default sandbox image setting.");

        return (image, null);
    }

    /// <summary>Union of the operator config array (Sandbox:AllowedImages — needs an app restart to
    /// change) and the tool setting's comma-separated list (ToolConfig "AdditionalAllowedImages" — live,
    /// no restart needed). Deduplicated so the same image listed in both doesn't show up twice in an error
    /// message; empty entries from stray commas/whitespace are dropped. An empty result (nothing configured
    /// in either place) means "any image allowed" — same as the original Sandbox:AllowedImages-only
    /// behaviour, preserved here rather than only satisfied by one specific source.</summary>
    private List<string> EffectiveAllowedImages()
    {
        var result = new List<string>(settings.Value.AllowedImages);

        var additional = configCache.GetValue("sandbox", "AdditionalAllowedImages");
        if (!string.IsNullOrWhiteSpace(additional))
        {
            foreach (var entry in additional.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!result.Contains(entry, StringComparer.Ordinal))
                    result.Add(entry);
            }
        }

        return result;
    }

    private async Task<ToolExecutionResult> ExecAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var command = arguments.GetStringArrayArg("command");
        if (command is null || command.Count == 0)
            return new ToolCompleted("'command' (a non-empty array of argv strings) is required for exec.", IsError: true);

        if (!context.PreApproved && settings.Value.RequireApprovalForDangerousCommands)
        {
            var joined = string.Join(' ', command);
            var matchedPattern = FindDangerousPattern(joined);
            if (matchedPattern is not null)
            {
                // Same mechanism the file tools use for a workspace escape: ChatAgentService pauses the
                // session and shows an approve/deny prompt; SkillExecutionService/AgentExecutionService
                // (no user to ask) reject this outright instead, so an unattended run never executes a
                // flagged command no matter how this is answered here.
                return new ToolApprovalRequired(
                    "This command matches a pattern flagged as a package install or a broadly destructive " +
                    "operation, so it needs your explicit approval before it runs in the sandbox.",
                    joined);
            }
        }

        var workingDir = arguments.GetStringArg("working_dir");
        var timeoutSeconds = Math.Clamp(
            arguments.GetIntArg("timeout_seconds") ?? settings.Value.DefaultExecTimeoutSeconds,
            1, Math.Max(1, settings.Value.MaxExecTimeoutSeconds));

        var result = await ExecWithSelfHealAsync(
            owner, sandboxId, provisioning, outcome, command, workingDir, TimeSpan.FromSeconds(timeoutSeconds), ct);

        // A null exit code with no timeout means the command never actually ran — a hard broker/Docker
        // failure (container missing and the self-heal retry above also failed, a transport error, ...) —
        // not a legitimate empty-output success. Surfacing it as an error with the real message is the fix
        // for what used to render as a silent "Exit code: unknown" with blank stdout/stderr.
        if (result.ExitCode is null && !result.TimedOut)
            return new ToolCompleted(
                $"The sandbox command failed to run: {result.Error ?? "no diagnostic information was returned."}",
                IsError: true);

        // A non-zero exit is not a tool failure — the command ran and the model can see the exit code and
        // react, the same posture HttpTool takes with a non-2xx HTTP status.
        return new ToolCompleted(RenderExec(result), IsError: false);
    }

    /// <summary>Returns the first pattern (from Sandbox:DangerousCommandPatterns) the joined command
    /// matches, or null. A malformed regex an operator added is skipped rather than thrown, the same
    /// lenient-on-bad-config posture ParsePorts/ParsePortsLabel take elsewhere in this feature — a typo in
    /// one pattern shouldn't take the whole safety net down.</summary>
    private string? FindDangerousPattern(string joinedCommand)
    {
        foreach (var pattern in settings.Value.DangerousCommandPatterns)
        {
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(joinedCommand, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return pattern;
            }
            catch (ArgumentException)
            {
                // Malformed pattern in config -- skip it rather than fail every exec call.
            }
        }
        return null;
    }

    /// <summary>Exit code FIRST, then stdout, then stderr — history trimming (Agent:ToolResultMaxChars)
    /// truncates a tool result from the END, so the exit code must never land somewhere that gets cut off.</summary>
    private string RenderExec(SandboxExecResult result)
    {
        var maxChars = Math.Max(0, settings.Value.MaxOutputCharsPerStream);
        var sb = new StringBuilder();
        sb.Append("Exit code: ").Append(result.ExitCode?.ToString() ?? "unknown");
        // The timeout notice belongs on the FIRST line for the same reason the exit code does:
        // appending it after stderr would put the single most important fact about the run in the
        // exact position history trimming cuts from, so it would vanish precisely when the output
        // is long enough for that to matter.
        if (result.TimedOut)
            sb.Append(" (command timed out and was terminated; output below is incomplete)");
        sb.Append('\n');
        sb.Append("--- stdout ---\n").Append(RenderStream(result.Stdout, result.StdoutTruncated, maxChars)).Append('\n');
        sb.Append("--- stderr ---\n").Append(RenderStream(result.Stderr, result.StderrTruncated, maxChars));
        return sb.ToString();
    }

    /// <summary>Caps defensively even though ISandboxHostClient already bounds each stream at the same
    /// limit — the contract doesn't guarantee every implementation does, and this is what the immediate
    /// ToolResultEvent pushed over SSE (never capped by history trimming) actually relies on. Tail-preserving:
    /// for shell output the error is usually at the end, so a forced cut keeps the LAST characters, not the
    /// first.</summary>
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

    private async Task<ToolExecutionResult> ReadFileAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for read_file.", IsError: true);

        var offset = Math.Max(0, arguments.GetIntArg("offset") ?? 0);
        var limit = Math.Max(0, arguments.GetIntArg("limit") ?? 0);

        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome, new FileOpRequest(
            Guid.NewGuid(), sandboxId, FileOp.Read, path,
            Offset: offset, Limit: limit, MaxBytes: settings.Value.MaxFileTransferBytes), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to read {path}.", IsError: true);

        var content = DecodeUtf8(response.ContentBase64);
        var note = response.Truncated ? "\n[... output truncated, use offset/limit to read more]" : "";
        return new ToolCompleted($"[{path}]\n{content}{note}");
    }

    private async Task<ToolExecutionResult> WriteFileAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
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

        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome, new FileOpRequest(
            Guid.NewGuid(), sandboxId, FileOp.Write, path,
            ContentBase64: Convert.ToBase64String(bytes), Append: append), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to write {path}.", IsError: true);

        return new ToolCompleted($"{(append ? "Appended" : "Wrote")} {bytes.Length} bytes to {path}.");
    }

    private async Task<ToolExecutionResult> EditFileAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
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

        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome, new FileOpRequest(
            Guid.NewGuid(), sandboxId, FileOp.Edit, path, OldText: oldText, NewText: newText), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to edit {path}.", IsError: true);

        // Reports the count rather than echoing content back, same contract as EditFileTool — lets the
        // model tell a no-op (0) from a hit without re-reading the file.
        return new ToolCompleted($"Replaced {response.ReplacementCount} occurrence(s) in {path}.");
    }

    private async Task<ToolExecutionResult> ListFilesAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path)) path = ".";
        var recursive = GetBoolArg(arguments, "recursive") ?? false;

        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome, new FileOpRequest(
            Guid.NewGuid(), sandboxId, FileOp.List, path, Recursive: recursive), ct);
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

    private async Task<ToolExecutionResult> SearchFilesAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
    {
        var pattern = arguments.GetStringArg("pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            return new ToolCompleted("'pattern' is required for search_files.", IsError: true);

        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path)) path = ".";

        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome, new FileOpRequest(
            Guid.NewGuid(), sandboxId, FileOp.Search, path, Pattern: pattern), ct);
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

    private async Task<ToolExecutionResult> DeleteAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for delete.", IsError: true);

        var recursive = GetBoolArg(arguments, "recursive") ?? false;
        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome, new FileOpRequest(
            Guid.NewGuid(), sandboxId, FileOp.Delete, path, Recursive: recursive), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to delete {path}.", IsError: true);

        return new ToolCompleted($"Deleted {path}.");
    }

    private async Task<ToolExecutionResult> MoveAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for move.", IsError: true);

        var destination = arguments.GetStringArg("destination");
        if (string.IsNullOrWhiteSpace(destination))
            return new ToolCompleted("'destination' is required for move.", IsError: true);

        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome, new FileOpRequest(
            Guid.NewGuid(), sandboxId, FileOp.Move, path, DestinationPath: destination), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to move {path}.", IsError: true);

        return new ToolCompleted($"Moved {path} to {destination}.");
    }

    private async Task<ToolExecutionResult> MkdirAsync(
        SandboxOwner owner, Guid sandboxId, SandboxProvisioningService provisioning, SelfHealOutcome outcome,
        JsonElement arguments, CancellationToken ct)
    {
        var path = arguments.GetStringArg("path");
        if (string.IsNullOrWhiteSpace(path))
            return new ToolCompleted("'path' is required for mkdir.", IsError: true);

        var response = await FileOpWithSelfHealAsync(owner, sandboxId, provisioning, outcome,
            new FileOpRequest(Guid.NewGuid(), sandboxId, FileOp.MakeDirectory, path), ct);
        if (!response.Success)
            return new ToolCompleted(response.Error ?? $"Failed to create directory {path}.", IsError: true);

        return new ToolCompleted($"Created directory {path}.");
    }

    private static string DecodeUtf8(string? base64) =>
        string.IsNullOrEmpty(base64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    private static bool? GetBoolArg(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;
}
