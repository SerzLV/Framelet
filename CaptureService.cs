using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
        return Forms.Screen.AllScreens
            .OrderByDescending(display => display.Bounds.Contains(CursorPosition))
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
            throw new InvalidOperationException(UiText.Get("PickWindow"));
        }

        if (Native.DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<Native.WindowRect>()) != 0
            && !Native.GetWindowRect(window, out rect))
        {
            throw new InvalidOperationException(UiText.Get("WindowBoundsFailure"));
        }

        var bounds = Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        bounds.Intersect(Forms.SystemInformation.VirtualScreen);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(UiText.Get("WindowOffscreen"));
        }

        return Capture(bounds);
    }

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


