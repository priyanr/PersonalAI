using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PersonalAI.Core.Tools.ComputerControl.Native;

/// <summary>P/Invoke surface for whole-desktop mouse/keyboard input (SendInput) and screen capture
/// (GDI BitBlt), scoped to the entire virtual screen rather than any single tracked window/process.
/// This mirrors the low-level mechanics proven by the standalone control-exe tool
/// (a separate Win32 window-automation utility), but is not a wrapper around it — control-exe is
/// window-handle-oriented and has no mouse support; this is desktop-wide and adds mouse control.</summary>
[SupportedOSPlatform("windows")]
internal static class Win32
{
    // --- GetSystemMetrics indices for full multi-monitor virtual-screen bounds ---
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    // --- SendInput: keyboard + mouse ---

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static readonly int InputSize = Marshal.SizeOf<INPUT>();

    // --- Mouse event flags ---
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;

    /// <summary>Standard one-notch wheel delta (WHEEL_DELTA).</summary>
    public const int WHEEL_DELTA = 120;

    // --- Keyboard event flags ---
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")]
    public static extern short VkKeyScanEx(char ch, nint dwhkl);

    [DllImport("user32.dll")]
    public static extern nint GetKeyboardLayout(uint idThread);

    // --- GDI screen capture (whole desktop via GetDC(IntPtr.Zero)) ---

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint hObject);

    public const uint SRCCOPY = 0x00CC0020;
}
