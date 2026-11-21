using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;

namespace Framelet;

internal sealed class SelectionWindow : Window
{
    internal SelectionWindow(ScreenSnapshot snapshot, bool activate, string promptKey, string adjustPromptKey,
        RegionSelection session, Action<Drawing.Rectangle> selected, Action<Drawing.Rectangle>? copy, Action canceled)
        : this(snapshot, activate, new SelectionSurface(snapshot, UiText.Get(promptKey), UiText.Get(adjustPromptKey),
            canceled, session, selected), canceled)
    {
        PreviewKeyDown += (_, e) =>
        {
            if (session.IsDragging || session.Selection is not { } region)
                return;

            var modifiers = Keyboard.Modifiers;
            var step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            var delta = e.Key switch
            {
                Key.Left => new Drawing.Point(-step, 0),
                Key.Right => new Drawing.Point(step, 0),
                Key.Up => new Drawing.Point(0, -step),
                Key.Down => new Drawing.Point(0, step),
                _ => Drawing.Point.Empty
            };
            if (e.Key == Key.Enter)
                selected(region);
            else if (e.Key == Key.C && modifiers == ModifierKeys.Control && copy is not null)
                copy(region);
            else if (!delta.IsEmpty)
                session.Nudge(delta.X, delta.Y);
            else
                return;
            e.Handled = true;
        };
    }

    internal SelectionWindow(ScreenSnapshot snapshot, string promptKey,
        Action<Int32Rect> selected, Action canceled, Int32Rect extension)
        : this(snapshot, true, new SelectionSurface(snapshot, UiText.Get(promptKey), canceled, extension, selected), canceled)
    {
    }

    private SelectionWindow(ScreenSnapshot snapshot, bool activate, SelectionSurface surface, Action canceled)
    {
        Title = UiText.Get("SelectionTitle");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = activate;
        ShowActivated = activate;
        Topmost = true;
        Background = Brushes.Black;
        Content = surface;
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
    private static readonly Pen CornerPen = FrozenPen(Ink, 1);
    private readonly ScreenSnapshot _snapshot;
    private readonly string _prompt;
    private readonly string? _adjustPrompt;
    private readonly Action _canceled;
    private readonly RegionSelection? _session;
    private readonly Action<Drawing.Rectangle>? _selected;
    private readonly Action<Int32Rect>? _tailSelected;
    private readonly Int32Rect? _extension;
    private Point? _tailPointer;

    internal SelectionSurface(ScreenSnapshot snapshot, string prompt, string adjustPrompt, Action canceled,
        RegionSelection session, Action<Drawing.Rectangle> selected) : this(snapshot, prompt, canceled)
    {
        _session = session;
        _selected = selected;
        _adjustPrompt = adjustPrompt;
        Loaded += (_, _) =>
        {
            session.Changed += UpdateRegionVisual;
            UpdateRegionVisual();
        };
        Unloaded += (_, _) => session.Changed -= UpdateRegionVisual;
    }

    internal SelectionSurface(ScreenSnapshot snapshot, string prompt, Action canceled,
        Int32Rect extension, Action<Int32Rect> selected) : this(snapshot, prompt, canceled)
    {
        _extension = extension;
        _tailSelected = selected;
    }

    private SelectionSurface(ScreenSnapshot snapshot, string prompt, Action canceled)
    {
        _snapshot = snapshot;
        _prompt = prompt;
        _canceled = canceled;
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Loaded += (_, _) =>
        {
            var position = CaptureService.CursorPosition;
            if (snapshot.Bounds.Contains(position))
            {
                if (_session is { } session)
                    session.MovePointer(position);
                else
                    _tailPointer = PointFromScreen(new Point(position.X, position.Y));
                Keyboard.Focus(this);
                InvalidateVisual();
            }
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!CaptureMouse())
            return;

        if (_session is { } session)
        {
            var point = CaptureService.CursorPosition;
            session.Begin(point, session.CornerAt(point, ResizeHitRadius()), PhysicalDistance(RegionSelection.DragThresholdDips));
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_session is { } session)
            session.MovePointer(CaptureService.CursorPosition);
        else
        {
            _tailPointer = e.GetPosition(this);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_session is { } session)
            session.MovePointer(CaptureService.CursorPosition);
        else if (!IsMouseCaptured)
        {
            _tailPointer = null;
            InvalidateVisual();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _session?.CancelDrag();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _canceled();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured)
            return;

        var confirmed = _session?.End(CaptureService.CursorPosition);
        ReleaseMouseCapture();
        if (confirmed is { } region)
            _selected?.Invoke(region);
        if (_extension is { } range)
        {
            var strip = ExtensionAt(range, e.GetPosition(this));
            if (strip.Width >= 3 && strip.Height >= 3)
                _tailSelected?.Invoke(strip);
        }

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
            var strip = _tailPointer is { } pointer ? ExtensionAt(extension, pointer) : extension with { Height = 0 };
            var xScale = bounds.Width / _snapshot.Bounds.Width;
            var yScale = bounds.Height / _snapshot.Bounds.Height;
            var selection = new Rect(strip.X * xScale, strip.Y * yScale, strip.Width * xScale, strip.Height * yScale);
            DrawSelection(drawing, bounds, selection, selection);
            drawing.DrawLine(SelectionPen, selection.TopLeft, selection.TopRight);
            DrawPrompt(drawing, bounds, _prompt);
            return;
        }

        var session = _session!;
        var cursorHere = _snapshot.Bounds.Contains(session.Pointer);
        var preview = session.Preview;
        if (preview is { } region && region.IntersectsWith(_snapshot.Bounds))
        {
            var outline = LocalRect(region);
            var selection = Rect.Intersect(outline, bounds);
            DrawSelection(drawing, bounds, selection, outline);
            if (session.IsDragging || session.Selection is not null)
            {
                if (cursorHere)
                {
                    var text = Text($"{region.Width} × {region.Height}", 12, Ink);
                    var badgeTop = selection.Top >= 86 ? selection.Top - 28 : selection.Bottom + 8;
                    DrawBadge(drawing, text, new Point(selection.Left, badgeTop), Accent);
                }
                DrawCorners(drawing, region, session);
            }
        }
        else
            drawing.DrawRectangle(Shade, null, bounds);

        if (!cursorHere || session.IsDragging)
            return;

        if (preview is null)
        {
            var position = PointFromScreen(new Point(session.Pointer.X, session.Pointer.Y));
            drawing.DrawLine(CrosshairPen, new Point(0, position.Y), new Point(Math.Max(0, position.X - 9), position.Y));
            drawing.DrawLine(CrosshairPen, new Point(Math.Min(bounds.Width, position.X + 9), position.Y), new Point(bounds.Width, position.Y));
            drawing.DrawLine(CrosshairPen, new Point(position.X, 0), new Point(position.X, Math.Max(0, position.Y - 9)));
            drawing.DrawLine(CrosshairPen, new Point(position.X, Math.Min(bounds.Height, position.Y + 9)), new Point(position.X, bounds.Height));
            DrawMagnifier(drawing, position);
        }
        DrawPrompt(drawing, bounds, session.Selection is null ? _prompt : _adjustPrompt ?? _prompt);
    }

    private void UpdateRegionVisual()
    {
        var session = _session!;
        var corner = session.ResizeCorner ?? session.CornerAt(session.Pointer, ResizeHitRadius());
        var cursor = corner switch
        {
            SelectionCorner.TopLeft or SelectionCorner.BottomRight => Cursors.SizeNWSE,
            SelectionCorner.TopRight or SelectionCorner.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Cross
        };
        if (Cursor != cursor)
            Cursor = cursor;
        InvalidateVisual();
    }

    private Drawing.Size ResizeHitRadius() => PhysicalDistance(9);

    private Drawing.Size PhysicalDistance(int dips)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Drawing.Size((int)Math.Ceiling(dips * dpi.DpiScaleX), (int)Math.Ceiling(dips * dpi.DpiScaleY));
    }

    private void DrawCorners(DrawingContext drawing, Drawing.Rectangle region, RegionSelection session)
    {
        var isAdjustable = session.Selection is not null;
        var hotCorner = session.ResizeCorner ?? session.CornerAt(session.Pointer, ResizeHitRadius());
        foreach (var corner in RegionSelection.Corners)
        {
            var local = LocalPoint(RegionSelection.CornerPosition(region, corner));
            var extent = !isAdjustable ? 5 : corner == hotCorner ? 11 : 9;
            var box = new Rect(local.X - extent / 2d, local.Y - extent / 2d, extent, extent);
            drawing.DrawRoundedRectangle(Accent, isAdjustable ? CornerPen : null, box, 2, 2);
        }
    }

    private static void DrawSelection(DrawingContext drawing, Rect bounds, Rect selection, Rect outline)
    {
        drawing.DrawRectangle(Shade, null, new Rect(0, 0, bounds.Width, selection.Top));
        drawing.DrawRectangle(Shade, null, new Rect(0, selection.Bottom, bounds.Width, Math.Max(0, bounds.Height - selection.Bottom)));
        drawing.DrawRectangle(Shade, null, new Rect(0, selection.Top, selection.Left, selection.Height));
        drawing.DrawRectangle(Shade, null, new Rect(selection.Right, selection.Top, Math.Max(0, bounds.Width - selection.Right), selection.Height));
        drawing.DrawRectangle(null, SelectionPen, outline);
    }

    private void DrawPrompt(DrawingContext drawing, Rect bounds, string message)
    {
        var prompt = Text(message, 14, Brushes.White);
        prompt.MaxTextWidth = Math.Max(1, bounds.Width - 36);
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

    private Point LocalPoint(Drawing.Point point) => new(
        (point.X - _snapshot.Bounds.Left) * ActualWidth / _snapshot.Bounds.Width,
        (point.Y - _snapshot.Bounds.Top) * ActualHeight / _snapshot.Bounds.Height);

    private Rect LocalRect(Drawing.Rectangle region) => new(LocalPoint(region.Location), new Size(
        region.Width * ActualWidth / _snapshot.Bounds.Width, region.Height * ActualHeight / _snapshot.Bounds.Height));

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


