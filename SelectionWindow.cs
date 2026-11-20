using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;

namespace Framelet;

internal sealed class SelectionWindow : Window
{
    internal SelectionWindow(ScreenSnapshot snapshot, bool activate, string promptKey,
        Action<ScreenSnapshot, Int32Rect> selected, Action canceled, Int32Rect? extension = null)
    {
        Title = UiText.Get("SelectionTitle");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = activate;
        ShowActivated = activate;
        Topmost = true;
        Background = Brushes.Black;
        Content = new SelectionSurface(snapshot, UiText.Get(promptKey), region => selected(snapshot, region), canceled, extension);
        var bounds = snapshot.Bounds;
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;

            e.Handled = true;
            canceled();
        };
    }
}

internal sealed class SelectionSurface : FrameworkElement
{
    private static readonly SolidColorBrush Shade = Brush("#8C071018");
    private static readonly SolidColorBrush Accent = Brush("#D3FA85");
    private static readonly SolidColorBrush Panel = Brush("#F21A242D");
    private static readonly SolidColorBrush Ink = Brush("#152015");
    private static readonly Pen SelectionPen = FrozenPen(Accent, 1.3);
    private static readonly Pen CrosshairPen = FrozenPen(Brush("#DFCFE4C4"), 1);
    private static readonly Pen DarkPen = FrozenPen(Brush("#B0182421"), 3);
    private readonly ScreenSnapshot _snapshot;
    private readonly string _prompt;
    private readonly Action<Int32Rect> _selected;
    private readonly Action _canceled;
    private readonly Int32Rect? _extension;
    private Point? _pointer;
    private Point? _start;

    internal SelectionSurface(ScreenSnapshot snapshot, string prompt, Action<Int32Rect> selected, Action canceled, Int32Rect? extension = null)
    {
        _snapshot = snapshot;
        _prompt = prompt;
        _selected = selected;
        _canceled = canceled;
        _extension = extension;
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Loaded += (_, _) =>
        {
            var position = CaptureService.CursorPosition;
            if (snapshot.Bounds.Contains(position))
            {
                _pointer = PointFromScreen(new Point(position.X, position.Y));
                Keyboard.Focus(this);
                InvalidateVisual();
            }
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!CaptureMouse())
            return;

        _start = _pointer = Clamp(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _pointer = Clamp(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_start is not null)
            return;

        _pointer = null;
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _start = null;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _canceled();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_start is not { } start)
            return;

        var end = Clamp(e.GetPosition(this));
        _start = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        var region = _extension is { } range ? ExtensionAt(range, end) : PhysicalRect(new Rect(start, end));
        if (region.Width >= 3 && region.Height >= 3)
            _selected(region);

        e.Handled = true;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        drawing.DrawImage(_snapshot.Image, bounds);
        if (_extension is { } extension)
        {
            var strip = _pointer is { } pointer ? ExtensionAt(extension, pointer) : extension with { Height = 0 };
            var xScale = bounds.Width / _snapshot.Bounds.Width;
            var yScale = bounds.Height / _snapshot.Bounds.Height;
            var selection = new Rect(strip.X * xScale, strip.Y * yScale, strip.Width * xScale, strip.Height * yScale);
            DrawSelection(drawing, bounds, selection);
            drawing.DrawLine(SelectionPen, selection.TopLeft, selection.TopRight);
            DrawPrompt(drawing, bounds);
            return;
        }
        if (_pointer is not { } position)
        {
            drawing.DrawRectangle(Shade, null, bounds);
            return;
        }

        if (_start is { } start)
        {
            var selection = new Rect(start, position);
            DrawSelection(drawing, bounds, selection);
            foreach (var corner in new[] { selection.TopLeft, selection.TopRight, selection.BottomLeft, selection.BottomRight })
                drawing.DrawRectangle(Accent, null, new Rect(corner.X - 2.5, corner.Y - 2.5, 5, 5));

            var pixels = PhysicalRect(selection);
            var text = Text($"{pixels.Width} × {pixels.Height}", 12, Ink);
            DrawBadge(drawing, text, new Point(selection.Left, selection.Top > 32 ? selection.Top - 30 : selection.Bottom + 6), Accent);
            return;
        }

        drawing.DrawRectangle(Shade, null, bounds);
        drawing.DrawLine(CrosshairPen, new Point(0, position.Y), new Point(Math.Max(0, position.X - 9), position.Y));
        drawing.DrawLine(CrosshairPen, new Point(Math.Min(bounds.Width, position.X + 9), position.Y), new Point(bounds.Width, position.Y));
        drawing.DrawLine(CrosshairPen, new Point(position.X, 0), new Point(position.X, Math.Max(0, position.Y - 9)));
        drawing.DrawLine(CrosshairPen, new Point(position.X, Math.Min(bounds.Height, position.Y + 9)), new Point(position.X, bounds.Height));
        DrawMagnifier(drawing, position);
        DrawPrompt(drawing, bounds);
    }

    private static void DrawSelection(DrawingContext drawing, Rect bounds, Rect selection)
    {
        drawing.DrawRectangle(Shade, null, new Rect(0, 0, bounds.Width, selection.Top));
        drawing.DrawRectangle(Shade, null, new Rect(0, selection.Bottom, bounds.Width, Math.Max(0, bounds.Height - selection.Bottom)));
        drawing.DrawRectangle(Shade, null, new Rect(0, selection.Top, selection.Left, selection.Height));
        drawing.DrawRectangle(Shade, null, new Rect(selection.Right, selection.Top, Math.Max(0, bounds.Width - selection.Right), selection.Height));
        drawing.DrawRectangle(null, SelectionPen, selection);
    }

    private void DrawPrompt(DrawingContext drawing, Rect bounds)
    {
        var prompt = Text(_prompt, 14, Brushes.White);
        DrawBadge(drawing, prompt, new Point((bounds.Width - prompt.Width - 18) / 2, 24), Panel);
    }

    private Int32Rect ExtensionAt(Int32Rect range, Point pointer)
    {
        var y = (int)Math.Ceiling(pointer.Y * _snapshot.Bounds.Height / ActualHeight);
        return range with { Height = Math.Clamp(y - range.Y, 0, range.Height) };
    }

    private void DrawMagnifier(DrawingContext drawing, Point pointer)
    {
        const double size = 110;
        const double zoom = 5;
        var x = pointer.X + 19 + size > ActualWidth ? pointer.X - size - 19 : pointer.X + 19;
        var y = pointer.Y + 19 + size > ActualHeight ? pointer.Y - size - 19 : pointer.Y + 19;
        var lens = new Rect(Math.Max(0, x), Math.Max(0, y), size, size);
        var center = new Point(lens.X + size / 2, lens.Y + size / 2);
        drawing.PushClip(new RectangleGeometry(lens, 7, 7));
        drawing.DrawRectangle(Panel, null, lens);
        drawing.DrawImage(_snapshot.Image, new Rect(center.X - pointer.X * zoom, center.Y - pointer.Y * zoom,
            ActualWidth * zoom, ActualHeight * zoom));

        var horizontalStart = new Point(center.X - 9, center.Y);
        var horizontalEnd = new Point(center.X + 9, center.Y);
        var verticalStart = new Point(center.X, center.Y - 9);
        var verticalEnd = new Point(center.X, center.Y + 9);
        drawing.DrawLine(DarkPen, horizontalStart, horizontalEnd);
        drawing.DrawLine(DarkPen, verticalStart, verticalEnd);
        drawing.DrawLine(SelectionPen, horizontalStart, horizontalEnd);
        drawing.DrawLine(SelectionPen, verticalStart, verticalEnd);
        drawing.Pop();
        drawing.DrawRoundedRectangle(null, SelectionPen, lens, 7, 7);
    }

    private Point Clamp(Point point)
    {
        return new Point(Math.Clamp(point.X, 0, ActualWidth), Math.Clamp(point.Y, 0, ActualHeight));
    }

    private Int32Rect PhysicalRect(Rect rect)
    {
        var xRatio = _snapshot.Bounds.Width / ActualWidth;
        var yRatio = _snapshot.Bounds.Height / ActualHeight;
        var left = Math.Clamp((int)Math.Floor(rect.Left * xRatio), 0, _snapshot.Bounds.Width);
        var top = Math.Clamp((int)Math.Floor(rect.Top * yRatio), 0, _snapshot.Bounds.Height);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right * xRatio), left, _snapshot.Bounds.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * yRatio), top, _snapshot.Bounds.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private FormattedText Text(string value, double size, Brush brush)
    {
        return new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private void DrawBadge(DrawingContext drawing, FormattedText text, Point position, Brush fill)
    {
        var width = text.Width + 18;
        var height = text.Height + 10;
        var left = Math.Clamp(position.X, 0, Math.Max(0, ActualWidth - width));
        var top = Math.Clamp(position.Y, 0, Math.Max(0, ActualHeight - height));
        drawing.DrawRoundedRectangle(fill, null, new Rect(left, top, width, height), 5, 5);
        drawing.DrawText(text, new Point(left + 9, top + 5));
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double width)
    {
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }
}


