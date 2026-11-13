using System.Runtime.InteropServices;

namespace WinNotch.Services;

/// <summary>
/// Global Esc listener (low-level keyboard hook). Used by gaming mode: while a
/// fullscreen app runs the notch stays hidden until Esc is pressed.
/// The key is never swallowed; it still reaches the game.
/// </summary>
public sealed class EscapeKeyService : IDisposable
{
    private delegate IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data);
    private readonly KeyboardCallback _callback;
    private IntPtr _hook;
    private bool _keyDown;
    public event Action? Pressed;

    public EscapeKeyService() => _callback = OnKeyboard;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _keyDown = false;
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _keyDown = false;
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Marshal.ReadInt32(data) == 0x1B)
        {
            int kind = message.ToInt32();
            if (kind is 0x100 or 0x104)
            {
                // Ignore auto-repeat while the key is held.
                if (!_keyDown)
                {
                    _keyDown = true;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Pressed?.Invoke());
                }
            }
            else if (kind is 0x101 or 0x105) _keyDown = false;
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, KeyboardCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
