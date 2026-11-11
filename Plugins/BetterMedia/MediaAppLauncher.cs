using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using WinNotch.Services;

namespace PersonalCards;

internal static class MediaAppLauncher
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);

    public static Action Bind(FrameworkElement cover, MediaService service, Action<string> log)
    {
        var oldCursor = cover.Cursor;
        var oldTip = cover.ToolTip;
        cover.Cursor = Cursors.Hand;
        cover.ToolTip = "Abrir la app que está reproduciendo";
        MouseButtonEventHandler clicked = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            e.Handled = true;
            try { Open(service); }
            catch (Exception ex) { log("Media: no pude abrir la app: " + ex.Message); }
        };
        cover.MouseLeftButtonUp += clicked;
        return () => { cover.MouseLeftButtonUp -= clicked; cover.Cursor = oldCursor; cover.ToolTip = oldTip; };
    }

    private static void Open(MediaService service)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var session = typeof(MediaService).GetField("_session", flags)?.GetValue(service)
            as Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
        string? source = session?.SourceAppUserModelId;
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("No hay una app multimedia activa.");
        // A desktop media session identifies the executable; prefer its existing window.
        string processName = System.IO.Path.GetFileNameWithoutExtension(source);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                if (IsIconic(handle)) ShowWindowAsync(handle, 9);
                if (SetForegroundWindow(handle)) return;
            }
        }
        // Packaged apps expose their AppUserModelId, which Windows can launch directly.
        if (source.Contains('!'))
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(@"shell:AppsFolder\" + source);
            Process.Start(start);
            return;
        }
        if (processName.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            return;
        }
        throw new InvalidOperationException("No encontré una ventana de " + source + ".");
    }
}
