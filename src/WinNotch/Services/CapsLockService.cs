using System.Runtime.InteropServices;

namespace WinNotch.Services;

public sealed class CapsLockService : IDisposable
{
    private delegate IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data);
    private readonly KeyboardCallback _callback;
    private IntPtr _hook;
    private bool _keyDown;
    private bool _enabled;
    public event Action<bool>? Changed;

    public CapsLockService() => _callback = OnKeyboard;
    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _enabled = (GetKeyState(0x14) & 1) != 0;
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
        // Only inspect Caps Lock; all input continues to its normal destination.
        if (code >= 0 && Marshal.ReadInt32(data) == 0x14)
        {
            int kind = message.ToInt32();
            if (kind is 0x100 or 0x104)
            {
                if (!_keyDown)
                {
                    _keyDown = true;
                    _enabled = !_enabled;
                    bool enabled = _enabled;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(enabled));
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
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
