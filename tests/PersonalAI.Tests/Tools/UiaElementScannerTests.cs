using System.Runtime.InteropServices;
using FluentAssertions;
using PersonalAI.Core.Tools.ComputerControl.Services;

namespace PersonalAI.Tests.Tools;

public class UiaElementScannerTests
{
    // Regression: the deployed API only references Microsoft.NETCore.App/AspNetCore.App, so FlaUI's UIA
    // interop couldn't load Accessibility.dll (part of Microsoft.WindowsDesktop.App) and every
    // `screenshot` action failed with "Could not load file or assembly 'Accessibility'".
    [Fact]
    public void Scan_LoadsUiAutomation_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var (text, _) = new UiaElementScanner().Scan();

        text.Should().NotContain("UI Automation unavailable");
    }
}
