namespace PersonalAI.Core.Tools.ComputerControl.Services;

/// <summary>Captures the full virtual screen (all monitors) to a PNG file. Injected by interface so
/// tests can substitute a fake — the real implementation does actual GDI screen capture and is not
/// unit-testable without a live interactive desktop.</summary>
public interface IScreenCaptureService
{
    /// <summary>Captures the whole virtual screen and saves it as a PNG. Returns the saved file's
    /// path plus its pixel dimensions.</summary>
    (string FilePath, int Width, int Height) CaptureFullScreen();
}
