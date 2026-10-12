namespace PersonalAI.Core.Tools.ComputerControl.Services;

/// <summary>Walks the UI Automation tree of the current foreground window (falling back to the whole
/// desktop) and renders visible, interactive elements as a compact indexed text block. This is how
/// the model "sees" the screen: PersonalAI's chat pipeline is text-only end-to-end (no image content
/// type anywhere, and DeepSeek has no vision model), so a raw screenshot cannot be handed to the
/// model directly — this element tree is the text substitute, deliberately chosen over OCR.</summary>
public interface IUiaElementScanner
{
    /// <summary>Returns the rendered element list plus how many elements it contains. An empty scan
    /// still returns a human-readable "no elements" message as Text, with Count 0.</summary>
    (string Text, int Count) Scan();
}
