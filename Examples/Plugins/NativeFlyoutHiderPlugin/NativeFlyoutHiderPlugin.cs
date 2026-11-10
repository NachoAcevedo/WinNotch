using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WinNotch.Plugins;

namespace PersonalCards;

/// <summary>
/// Oculta los avisos nativos de Windows (volumen, brillo, multimedia, modo avión, etc.: la
/// pastilla sobre la barra de tareas) mientras el HUD de volumen o de brillo de WinNotch esté
/// activado, para no verlos duplicados. Sin configuración: si se apagan ambos HUD en Ajustes,
/// se desactiva el plugin o se cierra WinNotch, vuelven los de Windows.
///
/// Mismo mecanismo que ModernFlyouts: el host de esos avisos es una ventana de explorer de
/// clase XamlExplorerHostIslandWindow (NativeHWNDHost antes de Windows 11 22H2) en la banda Z
/// "AboveLockUX" (18). Se minimiza su ventana interna de contenido
/// (DesktopWindowContentBridge / DirectUIHWND), así el aviso no llega a dibujarse aunque
/// explorer muestre el host; y además, por las dudas, se oculta el host apenas aparece
/// (hook EVENT_OBJECT_SHOW sobre explorer).
/// </summary>
[WinNotchPlugin("personal.flyouts.hidenative", "Ocultar avisos de Windows", "1.0.0", "Nacho_114")]
public sealed class NativeFlyoutHiderPlugin : PluginBase
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const int SwHide = 0;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint ZBandAboveLockUx = 18;

    // Clase del host y de su ventana interna de contenido, según la versión de Windows.
    private static readonly (string Host, string Content)[] FlyoutClasses =
    {
        ("XamlExplorerHostIslandWindow", "Windows.UI.Composition.DesktopWindowContentBridge"),
        ("NativeHWNDHost", "DirectUIHWND")
    };

    private WinEventProc? _proc;   // referencia viva para que el GC no libere el callback
    private IntPtr _hook;
    private uint _explorerPid;
    private IntPtr _minimizedContent;
    private DispatcherTimer? _watch;

    public override string Id => "personal.flyouts.hidenative";
    public override string Name => "Ocultar avisos de Windows";
    public override string Version => "1.0.0";
    public override string Author => "Nacho_114";
    public override string Description => "Oculta los avisos de volumen/brillo de Windows cuando el HUD de WinNotch está activado.";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();
        Application.Current.Dispatcher.Invoke(() =>
        {
            Hook();
            Apply();

            // Reengancha si explorer se reinicia, re-minimiza si recrea el aviso y
            // restaura los avisos de Windows si se apagan los HUD de WinNotch.
            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
            _watch.Tick += (_, _) =>
            {
                if (FindExplorerPid() != _explorerPid)
                {
                    Hook();
                }
                Apply();
            };
            _watch.Start();
        });
    }

    public override Task OnDisableAsync()
    {
        Application.Current.Dispatcher.Invoke(Stop);
        return base.OnDisableAsync();
    }

    public override Task ShutdownAsync()
    {
        Application.Current.Dispatcher.Invoke(Stop);
        return base.ShutdownAsync();
    }

    private void Stop()
    {
        _watch?.Stop();
        Unhook();
        RestoreNativeFlyout();
    }

    /// <summary>Minimiza o restaura el contenido del aviso nativo según los HUD de WinNotch.</summary>
    private void Apply()
    {
        if (!WinNotchHudEnabled())
        {
            RestoreNativeFlyout();
            return;
        }

        var (_, content) = FindNativeFlyout();
        if (content != IntPtr.Zero && !IsIconic(content))
        {
            ShowWindowAsync(content, SwMinimize);
        }
        _minimizedContent = content;
    }

    private void RestoreNativeFlyout()
    {
        if (_minimizedContent != IntPtr.Zero && IsWindow(_minimizedContent) && IsIconic(_minimizedContent))
        {
            ShowWindowAsync(_minimizedContent, SwRestore);
        }
        _minimizedContent = IntPtr.Zero;
    }

    /// <summary>Se engancha a explorer desde el hilo de UI (sus mensajes entregan los eventos).</summary>
    private void Hook()
    {
        Unhook();
        _explorerPid = FindExplorerPid();
        if (_explorerPid == 0)
        {
            return;
        }

        _proc ??= OnWinEvent;
        _hook = SetWinEventHook(EventObjectCreate, EventObjectShow, IntPtr.Zero, _proc, _explorerPid, 0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (_hook == IntPtr.Zero)
        {
            Context?.Log("Ocultar avisos de Windows: no se pudo enganchar a explorer", PluginLogLevel.Warning);
        }
    }

    private void Unhook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjIdWindow || idChild != 0 || hwnd == IntPtr.Zero || !WinNotchHudEnabled() || !IsFlyoutHost(hwnd))
        {
            return;
        }

        if (evt == EventObjectShow)
        {
            ShowWindowAsync(hwnd, SwHide);
        }
        Apply();   // si explorer recreó el aviso, minimiza el contenido nuevo
    }

    private bool WinNotchHudEnabled()
    {
        var settings = Context?.Settings;
        return settings != null && (settings.ShowVolumeHud || settings.ShowBrightnessHud);
    }

    /// <summary>Host del aviso nativo (de explorer, en la banda AboveLockUX) y su ventana de contenido.</summary>
    private (IntPtr Host, IntPtr Content) FindNativeFlyout()
    {
        foreach (var (hostClass, contentClass) in FlyoutClasses)
        {
            IntPtr host = IntPtr.Zero;
            while ((host = FindWindowEx(IntPtr.Zero, host, hostClass, null)) != IntPtr.Zero)
            {
                if (IsFlyoutHost(host))
                {
                    return (host, FindWindowEx(host, IntPtr.Zero, contentClass, null));
                }
            }
        }
        return (IntPtr.Zero, IntPtr.Zero);
    }

    private bool IsFlyoutHost(IntPtr hwnd)
    {
        var cls = new StringBuilder(64);
        if (GetClassName(hwnd, cls, cls.Capacity) == 0 || !FlyoutClasses.Any(c => c.Host == cls.ToString()))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == _explorerPid && GetWindowBand(hwnd, out uint band) && band == ZBandAboveLockUx;
    }

    private static uint FindExplorerPid()
    {
        // El explorer del escritorio es el dueño de la barra de tareas.
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
        {
            return 0;
        }
        GetWindowThreadProcessId(tray, out uint pid);
        return pid;
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    // No documentada: banda Z de la ventana (la usa ModernFlyouts para reconocer los avisos).
    [DllImport("user32.dll")]
    private static extern bool GetWindowBand(IntPtr hwnd, out uint band);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
