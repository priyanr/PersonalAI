namespace PersonalAI.Core.Models.Settings;

public class SandboxSettings
{
    public const string SectionName = "Sandbox";

    public bool Enabled { get; set; } = true;

    /// <summary>Shared secret the broker presents. Empty disables the endpoint entirely.</summary>
    public string HostKey { get; set; } = "";

    public string DefaultImage { get; set; } = "python:3.12-slim";

    /// <summary>Images a sandbox may use. Empty means "any" — the allow-list is the boundary.</summary>
    public string[] AllowedImages { get; set; } = ["python:3.12-slim", "node:22-slim", "debian:bookworm-slim"];

    /// <summary>Mount point inside the container. The host side is configured on the broker, not here.</summary>
    public string WorkDir { get; set; } = "/work";

    public int DefaultExecTimeoutSeconds { get; set; } = 60;

    public int MaxExecTimeoutSeconds { get; set; } = 600;

    /// <summary>When true (default), a command matching DangerousCommandPatterns pauses the sandbox tool
    /// with the same ToolApprovalRequired mechanism the file tools use for a workspace escape, instead of
    /// running immediately. Meaningful only in an interactive chat session — Skill/Agent runs already
    /// reject any ToolApprovalRequired outright (no user to ask), so a flagged command is simply refused
    /// there rather than paused. This is a heuristic speed bump, not a security boundary: once approved,
    /// the command still runs with the sandbox's full privileges, and no pattern list catches every
    /// dangerous invocation.</summary>
    public bool RequireApprovalForDangerousCommands { get; set; } = true;

    /// <summary>Case-insensitive regex patterns checked against the exec command joined with spaces, so
    /// both argv form (["pip","install","x"]) and a /bin/sh -c "..." string are caught the same way.
    /// Operator config only, deliberately not a live tool setting like AdditionalAllowedImages — this is
    /// a security posture, not a UX convenience, matching NetworkScan:AllowedRanges/HttpTool:AllowedUrls.
    /// The rm pattern only catches a single combined flag token (-rf, -Rf, -fr, -rfv, ...); split flags
    /// like "rm -r -f" are not — extend the list rather than expecting one entry to cover every case.</summary>
    public string[] DangerousCommandPatterns { get; set; } =
    [
        // apt/apt-get is deliberately separate: its "update" refreshes the package INDEX and installs
        // nothing (a routine, harmless command a model runs constantly before "apt-get install"), unlike
        // every other package manager here where "update" DOES mean "upgrade installed packages". Folding
        // it into the generic pattern below would flag "apt-get update" for approval every single time.
        @"\bapt(-get)?\b\s+(install|upgrade|dist-upgrade|full-upgrade)\b",
        @"\b(pip3?|npm|yarn|pnpm|gem|apk|yum|dnf|pacman|conda|cargo)\b\s+(install|add|update|upgrade)\b",
        @"\bsudo\b",
        @"\brm\s+-(?=\w*[rR])(?=\w*[fF])\w*\b",
        @"\bcurl\b[^|]*\|\s*(sh|bash|python3?)\b",
        @"\bwget\b[^|]*\|\s*(sh|bash|python3?)\b",
        @"\bchmod\s+(-R\s+)?[0-7]{3,4}\b",
        @"\bmkfs\b",
        @"\bdd\s+if=",
    ];

    /// <summary>Per-stream cap on exec output, deliberately under Agent:ToolResultMaxChars (4000) so a
    /// single command's output can't crowd out the rest of the conversation. Applied as head+tail (see
    /// BoundedOutputAccumulator) — the end of shell output is where the error is.</summary>
    public int MaxOutputCharsPerStream { get; set; } = 3000;

    public long MaxFileTransferBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Caps a single file download (the sandbox file browser's ReadRaw op). Larger than
    /// MaxFileTransferBytes since a download isn't constrained by the model's context budget; unlike
    /// Read/Write this never truncates — an oversized file fails outright rather than serving a corrupt
    /// partial download.</summary>
    public long MaxDownloadBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Caps the total UNCOMPRESSED size a single Zip op will archive, checked while walking the
    /// selection before the archive is built.</summary>
    public long MaxZipBytes { get; set; } = 50 * 1024 * 1024;

    public int MaxListEntries { get; set; } = 500;

    public int MaxSearchHits { get; set; } = 200;

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int ImagePullTimeoutSeconds { get; set; } = 300;   // first use may pull an image

    public int HeartbeatIntervalSeconds { get; set; } = 15;

    public int HeartbeatTimeoutSeconds { get; set; } = 45;

    public int IdleTimeoutMinutes { get; set; } = 60;         // retire idle chat containers (files kept)

    public int EphemeralGraceMinutes { get; set; } = 10;      // safety net only; normal teardown is explicit

    public int ReapPollSeconds { get; set; } = 60;

    /// <summary>Minimum gap between two calls to the sandbox tool's HostStartUrl setting — the machine
    /// running the Docker host may take a real minute or more to boot, so without this a model retrying
    /// the sandbox tool while it comes up would hit the start webhook on every single call.</summary>
    public int HostStartCooldownSeconds { get; set; } = 120;

    /// <summary>How often (in seconds) the sandbox tool polls ISandboxHostClient.IsConnected while waiting
    /// for a host that was just sent a start signal to boot and its broker to dial in.</summary>
    public int HostStartPollIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum seconds the sandbox tool will block waiting for the broker to connect after a
    /// start signal was sent (or while one is in cooldown). If the broker hasn't connected by then, the
    /// tool returns an error to the model. Set to 0 to disable waiting (revert to the old fail-fast
    /// behavior).</summary>
    public int HostStartMaxWaitSeconds { get; set; } = 120;

    /// <summary>How often to retry hitting the HostStartUrl during the wait loop if the host still
    /// hasn't connected.</summary>
    public int HostStartRetryIntervalSeconds { get; set; } = 30;

    public bool NetworkEnabled { get; set; } = true;

    // Unset by default on purpose — see Context.
    public int? CpuLimitMilliCores { get; set; }
    public long? MemoryLimitBytes { get; set; }
    public int? PidsLimit { get; set; }
    public bool RunAsNonRoot { get; set; }
    public string[] CapDrop { get; set; } = [];
}
