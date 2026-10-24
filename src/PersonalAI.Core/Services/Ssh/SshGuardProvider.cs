using PersonalAI.Core.Services.Tools;

namespace PersonalAI.Core.Services.Ssh;

/// <summary>Supplies the current <see cref="SshCommandGuard"/>, built from the ssh tool's "AllowedCommands"
/// setting (Settings &gt; Tools). The setting is read from the in-memory tool-config cache on every call, so an
/// edit takes effect immediately. Blank means the built-in default list.</summary>
public sealed class SshGuardProvider(ToolConfigCache cache)
{
    public const string ToolName = "ssh";
    public const string AllowedCommandsField = "AllowedCommands";

    public SshCommandGuard Current => new(SshCommandGuard.ParseAllowlist(cache.GetValue(ToolName, AllowedCommandsField)));
}
