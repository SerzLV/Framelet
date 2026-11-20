using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Framelet;

internal sealed class PrintScreenShortcut(Dispatcher dispatcher, Action capture) : IDisposable
{
    private const int Snapshot = 0x2C;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SysKeyDown = 0x0104;
    private const int SysKeyUp = 0x0105;
    private KeyboardCallback? _callback;
    private nint _hook;
    private bool _suppressUntilRelease;

    internal bool IsActive => _hook != 0;

    internal void Configure(bool enabled)
    {
        if (!enabled)
        {
            Dispose();
            return;
        }

        if (!IsActive)
        {
            // Windows can reserve Print Screen for Snipping Tool outside our own foreground window.
            _callback = OnKeyboard;
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        }
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code == 0 && Marshal.ReadInt32(data) == Snapshot)
        {
            switch ((int)message)
            {
                case KeyUp or SysKeyUp when _suppressUntilRelease:
                    _suppressUntilRelease = false;
                    return 1;

                case KeyDown or SysKeyDown:
                    if (_suppressUntilRelease)
                        return 1;

                    if (!HasModifiers())
                    {
                        _suppressUntilRelease = true;
                        dispatcher.BeginInvoke(DispatcherPriority.Input, capture);
                        return 1;
                    }
                    break;
            }
        }

        return CallNextHookEx(0, code, message, data);
    }

    private static bool HasModifiers()
    {
        return IsDown(0x10) || IsDown(0x11) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (IsActive)
            UnhookWindowsHookEx(_hook);

        _hook = 0;
        _callback = null;
        _suppressUntilRelease = false;
    }

    private delegate nint KeyboardCallback(int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, KeyboardCallback callback, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
