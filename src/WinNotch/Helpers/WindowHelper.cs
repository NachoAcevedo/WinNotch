using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinNotch.Helpers;

public static class WindowHelper
{
    // Extended window styles
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // Hide from Alt+Tab and taskbar
    private const int WS_EX_NOACTIVATE = 0x08000000;    // Don't steal focus on click
    private const int WS_EX_TRANSPARENT = 0x00000020;   // Click-through

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// Makes the window invisible in Alt+Tab and taskbar, and prevents it from stealing focus.
    /// Call after the window is loaded (SourceInitialized event).
    /// </summary>
    public static void MakeOverlayWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// Enables click-through on the transparent areas of the window.
    /// </summary>
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (clickThrough)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    public static void EnableTextInput(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
        window.Activate();
    }

    public static void KeepAbove(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    /// <summary>
    /// Excludes the window from screen capture APIs (BitBlt, CopyFromScreen, etc.).
    /// The window remains visible on the actual display but invisible to capture.
    /// Requires Windows 10 2004+ (build 19041).
    /// </summary>
    public static void SetExcludeFromCapture(Window window, bool exclude)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    /// <summary>
    /// Positions and sizes the window in PHYSICAL pixels, bypassing WPF's DIP
    /// coordinate conversion. Under Per-Monitor V2 this is the only unambiguous
    /// way to place a window on an arbitrary monitor; WPF rescales the content
    /// automatically after the resulting WM_DPICHANGED.
    /// </summary>
    public static void SetWindowRectPhysical(Window window, int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Crossing to a different-DPI monitor makes WM_DPICHANGED rescale the
        // window, overriding the size we pass. Move first so the DPI transition
        // settles, then apply the size on the destination monitor.
        var target = MonitorFromPoint(new POINT { x = x + width / 2, y = y + height / 2 }, MONITOR_DEFAULTTONEAREST);
        var current = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (target != current)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);

        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Moves the window (physical pixels) without resizing.</summary>
    public static void MoveWindowPhysical(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
    }

    /// <summary>Gets the window rect in physical pixels.</summary>
    public static System.Drawing.Rectangle GetWindowRectPhysical(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
            return System.Drawing.Rectangle.Empty;
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
}
