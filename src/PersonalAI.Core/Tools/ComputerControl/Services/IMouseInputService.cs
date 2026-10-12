namespace PersonalAI.Core.Tools.ComputerControl.Services;

public enum MouseButton
{
    Left,
    Right,
    Middle
}

/// <summary>Whole-desktop mouse control via SendInput with absolute coordinates normalized against
/// the full virtual screen (all monitors). Injected by interface into ComputerControlTool so tests
/// can substitute a fake — the real implementation does actual OS-level input and is not
/// unit-testable without a live interactive desktop.</summary>
public interface IMouseInputService
{
    /// <summary>Moves the cursor to the given screen coordinates (virtual-screen space, so a
    /// secondary monitor to the left of the primary may have negative x).</summary>
    void MoveTo(int x, int y);

    /// <summary>Clicks (or double-clicks) at the cursor's current position. Callers that want to
    /// click at specific coordinates should call MoveTo first.</summary>
    void Click(MouseButton button, bool doubleClick = false);

    /// <summary>Scrolls the mouse wheel. Positive amounts scroll up/away, negative scroll
    /// down/toward, matching the standard Win32 wheel-delta sign convention. The amount is in
    /// "notches" — each unit is scaled to one WHEEL_DELTA (120).</summary>
    void Scroll(int amount);

    /// <summary>Presses the left button at (fromX, fromY), moves to (toX, toY), then releases.</summary>
    void Drag(int fromX, int fromY, int toX, int toY);

    /// <summary>Current cursor position in virtual-screen coordinates.</summary>
    (int X, int Y) GetCursorPosition();
}
