using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Framelet;

internal enum CaptureMode
{
    Region,
    Screen,
    Window,
    Delayed,
    Scrolling,
    QuickClipboard
}

internal sealed record ScreenSnapshot(Drawing.Rectangle Bounds, BitmapSource Image);

internal static class CaptureService
{
    internal static Drawing.Point CursorPosition => Forms.Cursor.Position;

    internal static ScreenSnapshot[] CaptureDisplays()
    {
        var cursor = CursorPosition;
        return Forms.Screen.AllScreens
            .OrderByDescending(display => display.Bounds.Contains(cursor))
            .Select(display => new ScreenSnapshot(display.Bounds, Capture(display.Bounds)))
            .ToArray();
    }

    internal static BitmapSource CaptureCurrentDisplay()
    {
        return Capture(Forms.Screen.FromPoint(CursorPosition).Bounds);
    }

    internal static BitmapSource CaptureWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !Native.IsWindowVisible(window) || Native.IsIconic(window))
        {
            throw new UserFacingException("PickWindow");
        }

        if (!TryGetWindowBounds(window, out var bounds))
        {
            throw new UserFacingException("WindowBoundsFailure");
        }

        bounds.Intersect(Forms.SystemInformation.VirtualScreen);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new UserFacingException("WindowOffscreen");
        }

        return Capture(bounds);
    }

    internal static IReadOnlyList<Drawing.Rectangle> GetVisibleWindowBounds()
    {
        const int toolWindow = 0x00000080, noActivate = 0x08000000;
        var windows = new List<Drawing.Rectangle>();
        var shell = Native.GetShellWindow();
        Native.EnumWindows((window, _) =>
        {
            if (window != shell && Native.IsWindowVisible(window) && !Native.IsIconic(window)
                && (Native.GetExtendedWindowStyle(window) & (toolWindow | noActivate)) == 0
                && Native.GetWindowTextLength(window) > 0
                && (Native.GetDwmWindowState(window, 14, out var cloaked, sizeof(int)) != 0 || cloaked == 0)
                && TryGetWindowBounds(window, out var bounds) && bounds.Width >= 3 && bounds.Height >= 3)
                windows.Add(bounds);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool TryGetWindowBounds(IntPtr window, out Drawing.Rectangle bounds)
    {
        if (Native.DwmGetWindowAttribute(window, 9, out Native.WindowRect rect, Marshal.SizeOf<Native.WindowRect>()) == 0
            || Native.GetWindowRect(window, out rect))
        {
            bounds = Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }
        bounds = default;
        return false;
    }

    internal static BitmapSource CropDisplays(IReadOnlyList<ScreenSnapshot> screens, Drawing.Rectangle selection)
    {
        foreach (var screen in screens)
        {
            if (screen.Bounds.Contains(selection))
                return Crop(screen.Image, RelativeRect(selection, screen.Bounds));
        }

        var stride = checked(selection.Width * 4);
        var pixels = new byte[checked(stride * selection.Height)];
        foreach (var screen in screens)
        {
            var intersection = Drawing.Rectangle.Intersect(screen.Bounds, selection);
            if (intersection.Width <= 0 || intersection.Height <= 0)
                continue;

            var source = screen.Image.Format == PixelFormats.Bgr32
                ? screen.Image
                : new FormatConvertedBitmap(screen.Image, PixelFormats.Bgr32, null, 0);
            var offset = (intersection.Y - selection.Y) * stride + (intersection.X - selection.X) * 4;
            source.CopyPixels(RelativeRect(intersection, screen.Bounds), pixels, stride, offset);
        }

        var result = BitmapSource.Create(selection.Width, selection.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static Int32Rect RelativeRect(Drawing.Rectangle selection, Drawing.Rectangle relativeTo) =>
        new(selection.X - relativeTo.X, selection.Y - relativeTo.Y, selection.Width, selection.Height);

    internal static BitmapSource Crop(BitmapSource image, Int32Rect selection)
    {
        if (selection == new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight))
            return image;

        // A CroppedBitmap keeps the desktop-sized source alive for as long as the editor is open.
        var stride = checked((selection.Width * image.Format.BitsPerPixel + 7) / 8);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * selection.Height));
        image.CopyPixels(selection, pixels, stride, 0);
        var crop = BitmapSource.Create(selection.Width, selection.Height, image.DpiX, image.DpiY,
            image.Format, image.Palette, pixels, stride);
        crop.Freeze();
        return crop;
    }

    internal static BitmapSource Capture(Drawing.Rectangle bounds)
    {
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
        }

        var handle = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            Native.DeleteObject(handle);
        }
    }
}

internal static partial class Native
{
    internal const int HotkeyMessage = 0x0312;
    internal const uint ControlShift = 0x0002 | 0x0004 | 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool WindowEnumerator(IntPtr window, IntPtr state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(WindowEnumerator callback, IntPtr state);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(IntPtr window);

    internal static int GetExtendedWindowStyle(IntPtr window) => GetWindowLong(window, -20);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr window, int index);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr window, int id);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr window, out WindowRect rect);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr window, uint attribute, out WindowRect rect, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static partial int GetDwmWindowState(IntPtr window, uint attribute, out int value, int size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmFlush();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr objectHandle);
}


