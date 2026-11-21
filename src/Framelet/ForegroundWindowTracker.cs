using System.Runtime.InteropServices;

namespace Framelet;

internal sealed class ForegroundWindowTracker(IntPtr initialWindow) : IDisposable
{
    private Native.WinEventCallback? _callback;
    private IntPtr _hook;
    private IntPtr _lastWindow = initialWindow;

    internal void Start()
    {
        const uint foregroundEvent = 0x0003;
        const uint skipOwnProcess = 0x0002;
        _callback = (_, _, window, _, _, _, _) => Observe(window);
        _hook = Native.SetWinEventHook(foregroundEvent, foregroundEvent, IntPtr.Zero,
            Marshal.GetFunctionPointerForDelegate(_callback), 0, 0, skipOwnProcess);
        Observe(Native.GetForegroundWindow());
    }

    internal IntPtr Recall()
    {
        Observe(Native.GetForegroundWindow());
        return _lastWindow;
    }

    private void Observe(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return;

        Native.GetWindowThreadProcessId(window, out var process);
        if (process != 0 && process != Environment.ProcessId)
            _lastWindow = window;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _callback = null;
    }
}

internal static partial class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventCallback(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId,
        uint eventThread, uint eventTime);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, IntPtr callback,
        uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(IntPtr hook);
}
