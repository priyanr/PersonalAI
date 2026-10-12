using System.Runtime.Versioning;
using PersonalAI.Core.Tools.ComputerControl.Native;

namespace PersonalAI.Core.Tools.ComputerControl.Services;

[SupportedOSPlatform("windows")]
public sealed class MouseInputService : IMouseInputService
{
    public void MoveTo(int x, int y)
    {
        var (nx, ny) = NormalizeToVirtualScreen(x, y);
        SendMouseInput(nx, ny, 0, Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK);
    }

    public void Click(MouseButton button, bool doubleClick = false)
    {
        var (downFlag, upFlag) = button switch
        {
            MouseButton.Left => (Win32.MOUSEEVENTF_LEFTDOWN, Win32.MOUSEEVENTF_LEFTUP),
            MouseButton.Right => (Win32.MOUSEEVENTF_RIGHTDOWN, Win32.MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (Win32.MOUSEEVENTF_MIDDLEDOWN, Win32.MOUSEEVENTF_MIDDLEUP),
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };

        ClickOnce(downFlag, upFlag);
        if (doubleClick)
        {
            Thread.Sleep(40);
            ClickOnce(downFlag, upFlag);
        }
    }

    public void Scroll(int amount)
    {
        var delta = amount * Win32.WHEEL_DELTA;
        SendMouseInput(0, 0, (uint)delta, Win32.MOUSEEVENTF_WHEEL);
    }

    public void Drag(int fromX, int fromY, int toX, int toY)
    {
        MoveTo(fromX, fromY);
        Thread.Sleep(20);

        var (nx, ny) = NormalizeToVirtualScreen(fromX, fromY);
        SendMouseInput(nx, ny, 0, Win32.MOUSEEVENTF_LEFTDOWN | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK);
        Thread.Sleep(20);

        MoveTo(toX, toY);
        Thread.Sleep(20);

        var (nx2, ny2) = NormalizeToVirtualScreen(toX, toY);
        SendMouseInput(nx2, ny2, 0, Win32.MOUSEEVENTF_LEFTUP | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK);
    }

    public (int X, int Y) GetCursorPosition()
    {
        Win32.GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    private static void ClickOnce(uint downFlag, uint upFlag)
    {
        SendMouseInput(0, 0, 0, downFlag);
        Thread.Sleep(20);
        SendMouseInput(0, 0, 0, upFlag);
    }

    private static void SendMouseInput(int dx, int dy, uint mouseData, uint flags)
    {
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.InputUnion
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = 0
                }
            }
        };

        Win32.SendInput(1, [input], Win32.InputSize);
    }

    /// <summary>SendInput's absolute mouse mode requires coordinates scaled to a 0-65535 range. With
    /// MOUSEEVENTF_VIRTUALDESK, that range maps onto the full virtual screen (all monitors,
    /// potentially with a negative top-left origin) rather than just the primary monitor.</summary>
    private static (int NormX, int NormY) NormalizeToVirtualScreen(int x, int y)
    {
        var vLeft = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        var vTop = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        var vWidth = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        var vHeight = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        var normX = vWidth <= 0 ? 0 : (int)(((long)(x - vLeft) * 65536L) / vWidth);
        var normY = vHeight <= 0 ? 0 : (int)(((long)(y - vTop) * 65536L) / vHeight);
        return (normX, normY);
    }
}
