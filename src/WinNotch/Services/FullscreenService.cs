using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinNotch.Services;

/// <summary>
/// Detects when another application is running in fullscreen mode.
/// Used to auto-hide the notch when a fullscreen app is active.
/// </summary>
public class FullscreenService : IDisposable
{
    private DispatcherTimer? _timer;

    /// <summary>Whether a fullscreen application is currently detected.</summary>
    public bool IsFullscreen { get; private set; }

    /// <summary>Monitor handle hosting the fullscreen app (IntPtr.Zero when none).</summary>
    public IntPtr FullscreenMonitor { get; private set; }

    /// <summary>Fired when fullscreen state changes. Args: isFullscreen</summary>
    public event Action<bool>? FullscreenChanged;
    public event Action? ForegroundChanged;
    private IntPtr _lastForeground;

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
    }

    // Consecutive non-fullscreen checks required before showing the notch again.
    // Overlays, toasts, and alt-tab previews briefly steal the foreground while a
    // game runs fullscreen; without this debounce the notch flashes into view.
    private const int ClearStreakRequired = 3;
    private int _clearStreak;

    private void Check()
    {
        var foreground = GetForegroundWindow();
        if (foreground != _lastForeground)
        {
            _lastForeground = foreground;
            ForegroundChanged?.Invoke();
        }
        bool fullscreen = IsFullscreenAppRunning(out var monitor);

        if (fullscreen)
        {
            _clearStreak = 0;
            if (!IsFullscreen || monitor != FullscreenMonitor)
            {
                IsFullscreen = true;
                FullscreenMonitor = monitor;
                FullscreenChanged?.Invoke(true);
            }
        }
        else if (IsFullscreen && ++_clearStreak >= ClearStreakRequired)
        {
            IsFullscreen = false;
            FullscreenMonitor = IntPtr.Zero;
            FullscreenChanged?.Invoke(false);
        }
    }

    private static bool IsFullscreenAppRunning(out IntPtr fullscreenMonitor)
    {
        fullscreenMonitor = IntPtr.Zero;
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        // Ignore the desktop/shell window
        IntPtr desktop = GetDesktopWindow();
        IntPtr shell = GetShellWindow();
        if (foreground == desktop || foreground == shell) return false;

        // Clicking the desktop can foreground Progman/WorkerW, which are
        // monitor-sized — without this check the notch hides on desktop clicks.
        var className = new System.Text.StringBuilder(64);
        if (GetClassName(foreground, className, className.Capacity) > 0)
        {
            var cls = className.ToString();
            if (cls is "Progman" or "WorkerW") return false;
        }

        if (GetWindowRect(foreground, out RECT windowRect))
        {
            // Get the monitor info for the foreground window
            IntPtr monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var screen = monitorInfo.rcMonitor;
                bool covers = windowRect.Left <= screen.Left &&
                              windowRect.Top <= screen.Top &&
                              windowRect.Right >= screen.Right &&
                              windowRect.Bottom >= screen.Bottom;
                if (covers)
                {
                    fullscreenMonitor = monitor;
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        _timer?.Stop();
    }

    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    #endregion
}
