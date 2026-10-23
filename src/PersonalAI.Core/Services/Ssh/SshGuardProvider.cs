using PersonalAI.Core.Services.Tools;

namespace PersonalAI.Core.Services.Ssh;

/// <summary>Supplies the current <see cref="SshCommandGuard"/>, built from the ssh tool's "AllowedCommands"
/// setting (Settings &gt; Tools). The setting is read from the in-memory tool-config cache on every call, so an
/// edit takes effect immediately, and the guard is rebuilt only when the text actually changed.
/// Blank means the built-in default list.</summary>
public sealed class SshGuardProvider(ToolConfigCache cache)
{
    public const string ToolName = "ssh";
    public const string AllowedCommandsField = "AllowedCommands";

    private readonly object _lock = new();
    private string? _text;
    private SshCommandGuard? _guard;

    public SshCommandGuard Current
    {
        get
        {
            var text = cache.GetValue(ToolName, AllowedCommandsField);
            lock (_lock)
            {
                if (_guard is null || !string.Equals(text, _text, StringComparison.Ordinal))
                {
                    _guard = new SshCommandGuard(SshCommandGuard.ParseAllowlist(text));
                    _text = text;
                }
                return _guard;
            }
        }
    }
}
