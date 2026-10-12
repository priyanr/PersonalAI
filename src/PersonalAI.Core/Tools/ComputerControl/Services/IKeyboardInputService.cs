namespace PersonalAI.Core.Tools.ComputerControl.Services;

/// <summary>Whole-desktop keyboard input via SendInput. Sends to whatever currently has OS focus —
/// unlike control-exe's per-window KeyboardInputService, this deliberately does no
/// ForceForeground/window-targeting, since the tool is scoped to the whole desktop rather than one
/// tracked window.</summary>
public interface IKeyboardInputService
{
    /// <summary>Types literal text character-by-character, using VkKeyScanEx to find a virtual-key +
    /// modifier combination where possible and falling back to a synthetic Unicode key event
    /// (KEYEVENTF_UNICODE) for characters with no keyboard-layout mapping.</summary>
    void SendText(string text, int delayMs);

    /// <summary>Sends a sequence of key tokens, each either a single key ("ENTER", "A") or a
    /// '+'-joined chord ("CTRL+ALT+DELETE"). Named keys use the same table as control-exe's
    /// /api/keys endpoint.</summary>
    void SendKeySequence(IEnumerable<string> keys, int delayMs);
}
