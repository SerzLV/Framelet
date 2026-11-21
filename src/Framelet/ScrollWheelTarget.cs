using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Framelet;

internal sealed class ScrollWheelTarget
{
    private readonly Drawing.Point[] _points;
    private readonly IntPtr _window;

    internal ScrollWheelTarget(Drawing.Rectangle bounds)
    {
        var centerX = bounds.Left + bounds.Width / 2;
        var centerY = bounds.Top + bounds.Height / 2;
        _points = [
            new(centerX, centerY),
            new(centerX, bounds.Top + bounds.Height / 3),
            new(bounds.Left + bounds.Width / 3, centerY),
            new(centerX, bounds.Bottom - 1 - bounds.Height / 3),
            new(bounds.Right - 1 - bounds.Width / 3, centerY)
        ];
        _window = RootAt(_points[0]);
    }

    internal bool IsForeground => _window != IntPtr.Zero && Native.GetForegroundWindow() == _window;

    internal bool TryActivate() => IsForeground || (_window != IntPtr.Zero && Native.IsWindowVisible(_window)
        && !Native.IsIconic(_window) && Native.SetForegroundWindow(_window));

    internal int GetVisibleBottom(Drawing.Rectangle selection)
        => GetVisibleBounds(selection) is { } visible && visible.Contains(selection) ? visible.Bottom : selection.Bottom;

    private Drawing.Rectangle? GetVisibleBounds(Drawing.Rectangle selection)
    {
        if (_window == IntPtr.Zero || !Native.IsWindowVisible(_window) || Native.IsIconic(_window))
            return null;

        if (Native.DwmGetWindowAttribute(_window, 9, out var rect, Marshal.SizeOf<Native.WindowRect>()) != 0
            && !Native.GetWindowRect(_window, out rect))
            return null;

        var visible = Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        visible.Intersect(Forms.Screen.FromRectangle(selection).Bounds);
        return visible;
    }

    internal async Task<(int Bottom, bool AtEnd)?> GetDocumentEndAsync(Drawing.Rectangle selection)
    {
        if (!IsForeground)
            return null;

        try
        {
            return await Task.Run(() => ReadDocumentEnd(selection)).WaitAsync(TimeSpan.FromMilliseconds(900));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private (int Bottom, bool AtEnd)? ReadDocumentEnd(Drawing.Rectangle selection)
    {
        try
        {
            if (GetVisibleBounds(selection) is not { } window || !window.Contains(selection))
                return null;

            var point = _points.FirstOrDefault(point => RootAt(point) == _window);
            if (point.IsEmpty)
                return null;

            var element = AutomationElement.FromPoint(new Point(point.X, point.Y));
            (int Bottom, bool AtEnd)? document = null;
            for (var level = 0; level < 36 && element is not null; level++)
            {
                var current = element.Current;
                if (current.ControlType == ControlType.Document && !current.IsOffscreen
                    && element.TryGetCurrentPattern(ScrollPattern.Pattern, out var value) && value is ScrollPattern scroll)
                {
                    var state = scroll.Current;
                    var viewport = current.BoundingRectangle;
                    var selected = new Rect(selection.Left, selection.Top, selection.Width, selection.Height);
                    if (state.VerticallyScrollable && !viewport.IsEmpty && viewport.Contains(selected))
                        document = (Math.Min(window.Bottom, (int)Math.Floor(viewport.Bottom)), state.VerticalScrollPercent >= 99.9);
                }

                if (current.NativeWindowHandle != 0 && new IntPtr(current.NativeWindowHandle) == _window)
                    return document;
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // An accessibility provider can disappear between reading an element and its parent.
        }
        return null;
    }

    internal async Task<bool> TryScrollDownAsync(int pointOffset)
    {
        if (!CanAdvance())
            return false;

        for (var i = 0; i < _points.Length; i++)
        {
            var point = _points[(pointOffset + i) % _points.Length];
            if (RootAt(point) == _window)
                return await SendWheelAsync(point);
        }

        return false;
    }

    private bool CanAdvance()
    {
        const int shift = 0x10, control = 0x11, alt = 0x12;
        const int leftWindows = 0x5B, rightWindows = 0x5C;
        const int leftButton = 0x01, rightButton = 0x02, middleButton = 0x04;
        return IsForeground && !IsPressed(shift) && !IsPressed(control) && !IsPressed(alt)
            && !IsPressed(leftWindows) && !IsPressed(rightWindows)
            && !IsPressed(leftButton) && !IsPressed(rightButton) && !IsPressed(middleButton);
    }

    internal unsafe bool TryPressDown()
    {
        const ushort down = 0x28;
        const uint keyboard = 1, extendedKey = 1, keyUp = 2;
        if (!CanAdvance() || IsPressed(down))
            return false;

        var inputs = stackalloc Native.Input[2];
        inputs[0] = new Native.Input { Type = keyboard, Data = new() { Keyboard = new() { VirtualKey = down, Flags = extendedKey } } };
        inputs[1] = new Native.Input { Type = keyboard, Data = new() { Keyboard = new() { VirtualKey = down, Flags = extendedKey | keyUp } } };
        var sent = Native.SendInput(2, inputs, sizeof(Native.Input));
        if (sent == 1)
            Native.SendInput(1, inputs + 1, sizeof(Native.Input));
        return sent == 2;
    }

    private static IntPtr RootAt(Drawing.Point point)
    {
        const uint root = 2;
        return Native.GetAncestor(Native.WindowFromPoint(new Native.ScreenPoint { X = point.X, Y = point.Y }), root);
    }

    private static bool IsPressed(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private static async Task<bool> SendWheelAsync(Drawing.Point point)
    {
        var desktop = Forms.SystemInformation.VirtualScreen;
        var original = Forms.Cursor.Position;
        var sent = SendWheel(point, desktop);
        if (original != point)
        {
            // Some controls resolve the wheel target from the current pointer when processing the message.
            await Task.Delay(55);
            if (Forms.Cursor.Position == point)
                Native.SetCursorPos(original.X, original.Y);
        }
        return sent;
    }

    private static unsafe bool SendWheel(Drawing.Point point, Drawing.Rectangle desktop)
    {
        const uint wheel = 0x0800;
        var input = stackalloc Native.Input[2];
        input[0] = MoveTo(point, desktop);
        input[1] = new Native.Input { Data = new() { Mouse = new() { Flags = wheel, Data = -120 } } };
        return Native.SendInput(2, input, sizeof(Native.Input)) == 2;
    }

    private static Native.Input MoveTo(Drawing.Point point, Drawing.Rectangle desktop)
    {
        const uint moveAbsoluteVirtualDesktop = 0x0001 | 0x4000 | 0x8000;
        return new Native.Input
        {
            Data = new Native.InputData { Mouse = new Native.MouseInput
            {
                X = Normalize(point.X - desktop.X, desktop.Width),
                Y = Normalize(point.Y - desktop.Y, desktop.Height),
                Flags = moveAbsoluteVirtualDesktop
            } }
        };
    }

    private static int Normalize(int pixel, int size)
        => (int)Math.Clamp((pixel * 65536L + 32768) / Math.Max(1, size), 0, 65535);
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenPoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X;
        internal int Y;
        internal int Data;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputData Data;
    }

    [LibraryImport("user32.dll")]
    internal static partial IntPtr WindowFromPoint(ScreenPoint point);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr window, uint kind);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial uint SendInput(uint count, Input* inputs, int size);
}
