using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Tools.ComputerControl.Native;

namespace PersonalAI.Core.Tools.ComputerControl.Services;

[SupportedOSPlatform("windows")]
public sealed class ScreenCaptureService(IOptions<ToolOutputSettings> toolOutputSettings) : IScreenCaptureService
{
    public (string FilePath, int Width, int Height) CaptureFullScreen()
    {
        var left = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        var top = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        var width = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        var height = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        using var bitmap = CaptureRegion(left, top, width, height);

        var screenshotsDir = Path.Combine(Path.GetFullPath(toolOutputSettings.Value.RootPath), "screenshots");
        Directory.CreateDirectory(screenshotsDir);

        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
        var filePath = Path.Combine(screenshotsDir, fileName);
        bitmap.Save(filePath, ImageFormat.Png);

        return (filePath, width, height);
    }

    private static Bitmap CaptureRegion(int left, int top, int width, int height)
    {
        var screenDc = Win32.GetDC(nint.Zero);
        if (screenDc == nint.Zero)
            throw new InvalidOperationException("Failed to acquire a device context for the desktop.");

        var memoryDc = nint.Zero;
        var bitmapHandle = nint.Zero;
        var oldObject = nint.Zero;
        try
        {
            memoryDc = Win32.CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
                throw new InvalidOperationException("Failed to create a memory device context.");

            bitmapHandle = Win32.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmapHandle == nint.Zero)
                throw new InvalidOperationException("Failed to create a compatible bitmap.");

            oldObject = Win32.SelectObject(memoryDc, bitmapHandle);
            if (!Win32.BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, Win32.SRCCOPY))
                throw new InvalidOperationException("BitBlt failed while capturing the screen.");

            return Image.FromHbitmap(bitmapHandle);
        }
        finally
        {
            if (memoryDc != nint.Zero && oldObject != nint.Zero)
                Win32.SelectObject(memoryDc, oldObject);
            if (bitmapHandle != nint.Zero)
                Win32.DeleteObject(bitmapHandle);
            if (memoryDc != nint.Zero)
                Win32.DeleteDC(memoryDc);
            Win32.ReleaseDC(nint.Zero, screenDc);
        }
    }
}
