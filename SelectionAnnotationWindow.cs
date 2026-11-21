using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace Framelet;

internal sealed class SelectionAnnotationWindow : Window
{
    private readonly Drawing.Rectangle _bounds;
    internal EditorSurface Editor { get; } = new();

    internal SelectionAnnotationWindow(Drawing.Rectangle bounds, BitmapSource image)
    {
        _bounds = bounds;
        Title = UiText.Get("SelectionTitle");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        Editor.HorizontalAlignment = HorizontalAlignment.Left;
        Editor.VerticalAlignment = VerticalAlignment.Top;
        Content = new Border { Child = Editor, ClipToBounds = true };
        Editor.Reset(image);
        RenderOptions.SetBitmapScalingMode(Editor, BitmapScalingMode.NearestNeighbor);
        SourceInitialized += (_, _) => AlignToPixels();
        Loaded += (_, _) => AlignToPixels();
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(AlignToPixels, DispatcherPriority.Loaded);
    }

    internal void ShowCanvas()
    {
        if (!IsVisible)
            Show();
        AlignToPixels();
        Activate();
        Editor.Focus();
    }

    private void AlignToPixels()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        Native.SetWindowPos(handle, IntPtr.Zero, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0004 | 0x0010);
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = 1 / dpi.DpiScaleX;
        var scaleY = 1 / dpi.DpiScaleY;
        if (Editor.LayoutTransform is not ScaleTransform transform || transform.ScaleX != scaleX || transform.ScaleY != scaleY)
        {
            Editor.LayoutTransform = new ScaleTransform(scaleX, scaleY);
            Editor.SetZoom(Math.Min(scaleX, scaleY));
        }
    }
}
