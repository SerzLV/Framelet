using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Framelet;

internal sealed class PrintScreenShortcut(Dispatcher dispatcher, Action capture) : IDisposable
{
    private const int Snapshot = 0x2C;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const int SysKeyDown = 0x0104;
    private const int SysKeyUp = 0x0105;
    private Thread? _thread;
    private Dispatcher? _hookDispatcher;
    private bool _suppressUntilRelease;

    internal bool IsActive => _hookDispatcher is not null;

    internal void Configure(bool enabled)
    {
        if (!enabled)
        {
            Dispose();
            return;
        }

        if (IsActive)
            return;

        var ready = new TaskCompletionSource<Dispatcher?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunHook(ready)) { IsBackground = true, Name = "Framelet Print Screen" };
        thread.Start();
        _hookDispatcher = ready.Task.GetAwaiter().GetResult();
        if (_hookDispatcher is not null)
            _thread = thread;
    }

    private void RunHook(TaskCompletionSource<Dispatcher?> ready)
    {
        KeyboardCallback callback = OnKeyboard;
        nint hook = 0;
        try
        {
            // Windows can reserve Print Screen for Snipping Tool outside our own foreground window.
            var loop = Dispatcher.CurrentDispatcher;
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            ready.TrySetResult(hook == 0 ? null : loop);
            if (hook != 0)
                Dispatcher.Run();
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
        }
        finally
        {
            ready.TrySetResult(null);
            if (hook != 0)
                UnhookWindowsHookEx(hook);
            _suppressUntilRelease = false;
            GC.KeepAlive(callback);
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
        _hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        _thread?.Join();
        _hookDispatcher = null;
        _thread = null;
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
