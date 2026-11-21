using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelet;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void PinnedCaptureUsesConnectedDisplays(Application app)
    {
        const int width = 160, height = 100;
        var screens = Forms.Screen.AllScreens;
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        var owner = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = area.Left, Top = area.Top, Width = 400, Height = 300,
            ShowActivated = false, ShowInTaskbar = false
        };
        new WindowInteropHelper(owner).EnsureHandle();
        var image = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        var missingOrigin = new Drawing.Point(screens.Max(screen => screen.Bounds.Right) + 100, area.Top);
        var fallback = AuxiliaryWindows.ShowPin(owner, image, missingOrigin);
        try
        {
            Require(screens.Any(screen => screen.WorkingArea.IntersectsWith(PinBounds(fallback))),
                "A pin from a disconnected display opens on a connected display");
        }
        finally
        {
            fallback.Close();
        }

        var origin = new Drawing.Point(area.Left + 20, area.Top + 20);
        var pin = AuxiliaryWindows.ShowPin(owner, image, origin);
        try
        {
            pin.UpdateLayout();
            Require(PinBounds(pin) == new Drawing.Rectangle(origin.X, origin.Y, width, height),
                "A pin with a visible origin keeps its physical position and size");
            var content = (UIElement)pin.Content;
            var dpi = VisualTreeHelper.GetDpi(content);
            var rendered = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rendered.Render(content);
            var pixels = ToPixels(rendered);
            var accent = ((SolidColorBrush)app.FindResource("Accent")).Color;
            var canvas = ((SolidColorBrush)app.FindResource("Canvas")).Color;
            var row = height / 2 * width * 4;
            Require(IsPixel(0, canvas) && Enumerable.Range(1, 8).Any(x => IsPixel(x, accent)) && IsPixel(width / 2, Colors.Black),
                "The visible pin frame outlines the original image in place");

            bool IsPixel(int x, Color color) => pixels.AsSpan(row + x * 4, 4).SequenceEqual([color.B, color.G, color.R, color.A]);
        }
        finally
        {
            pin.Close();
            owner.Close();
        }
    }

    private static Drawing.Rectangle PinBounds(Window window)
    {
        if (!Native.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect))
            throw new InvalidOperationException("Could not get pinned capture bounds.");
        return Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}
