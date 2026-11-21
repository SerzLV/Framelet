using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Framelet;

public sealed class EditorCropLayer : FrameworkElement
{
    private static readonly Brush Accent = CreateBrush(Color.FromRgb(211, 250, 133));
    private static readonly Brush Shade = CreateBrush(Color.FromArgb(155, 8, 14, 20));
    private Rect _imageBounds;
    private Rect _selection;
    private Rect _startSelection;
    private Point? _anchor;
    private DragEdges _drag;
    private double _zoom = 1;

    public EditorCropLayer()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    internal event Action? SelectionChanged;
    internal bool HasCrop => _selection.Width > 0 && _selection.Height > 0 && _selection != _imageBounds;
    internal Int32Rect PixelSelection => new((int)_selection.X, (int)_selection.Y, (int)_selection.Width, (int)_selection.Height);

    internal void Begin(double width, double height)
    {
        _imageBounds = new Rect(0, 0, width, height);
        SetSelection(_imageBounds);
        Focus();
    }

    internal void End()
    {
        _anchor = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    internal void SetZoom(double zoom)
    {
        _zoom = zoom;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = Clamp(e.GetPosition(this));
        if (!CaptureMouse())
            return;

        Focus();
        _anchor = point;
        _startSelection = _selection;
        _drag = DragAt(point);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = Clamp(e.GetPosition(this));
        if (_anchor is { } start)
            UpdateDrag(start, point);
        else
            Cursor = DragAt(point) switch
            {
                DragEdges.Left or DragEdges.Right => Cursors.SizeWE,
                DragEdges.Top or DragEdges.Bottom => Cursors.SizeNS,
                DragEdges.TopLeft or DragEdges.BottomRight => Cursors.SizeNWSE,
                DragEdges.TopRight or DragEdges.BottomLeft => Cursors.SizeNESW,
                DragEdges.Move => Cursors.SizeAll,
                _ => Cursors.Cross
            };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_anchor is not { } start)
            return;

        UpdateDrag(start, Clamp(e.GetPosition(this)));
        End();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _anchor = null;

    protected override void OnRender(DrawingContext drawing)
    {
        // The clear area also accepts mouse input.
        drawing.DrawRectangle(Brushes.Transparent, null, _imageBounds);
        drawing.DrawRectangle(Shade, null, new Rect(0, 0, _imageBounds.Width, _selection.Top));
        drawing.DrawRectangle(Shade, null, new Rect(0, _selection.Bottom, _imageBounds.Width, _imageBounds.Bottom - _selection.Bottom));
        drawing.DrawRectangle(Shade, null, new Rect(0, _selection.Top, _selection.Left, _selection.Height));
        drawing.DrawRectangle(Shade, null, new Rect(_selection.Right, _selection.Top, _imageBounds.Right - _selection.Right, _selection.Height));

        var unit = 1 / _zoom;
        var border = new Pen(Accent, 1.5 * unit);
        drawing.DrawRectangle(null, border, _selection);
        var grip = Math.Min(7 * unit, Math.Min(_selection.Width, _selection.Height) / 3);
        var middleX = _selection.X + _selection.Width / 2;
        var middleY = _selection.Y + _selection.Height / 2;
        foreach (var point in new[]{_selection.TopLeft, new Point(middleX, _selection.Top), _selection.TopRight,
            new Point(_selection.Left, middleY), new Point(_selection.Right, middleY),
            _selection.BottomLeft, new Point(middleX, _selection.Bottom), _selection.BottomRight})
            drawing.DrawRoundedRectangle(Accent, null, new Rect(point.X - grip / 2, point.Y - grip / 2, grip, grip), unit, unit);
    }

    private DragEdges DragAt(Point point)
    {
        var tolerance = Math.Min(9 / _zoom, Math.Min(_selection.Width, _selection.Height) / 3);
        var near = _selection;
        near.Inflate(tolerance, tolerance);
        if (!near.Contains(point))
            return DragEdges.New;

        var edges = DragEdges.New;
        if (Math.Abs(point.X - _selection.Left) <= tolerance) edges |= DragEdges.Left;
        if (Math.Abs(point.X - _selection.Right) <= tolerance) edges |= DragEdges.Right;
        if (Math.Abs(point.Y - _selection.Top) <= tolerance) edges |= DragEdges.Top;
        if (Math.Abs(point.Y - _selection.Bottom) <= tolerance) edges |= DragEdges.Bottom;
        return edges == DragEdges.New && HasCrop ? DragEdges.Move : edges;
    }

    private void UpdateDrag(Point start, Point point)
    {
        if (_drag == DragEdges.New)
        {
            var next = new Rect(start, point);
            if (next.Width >= 1 && next.Height >= 1)
                SetSelection(next);
            return;
        }

        var delta = point - start;
        var rect = _startSelection;
        if (_drag == DragEdges.Move)
        {
            rect.Offset(Math.Clamp(delta.X, -rect.Left, _imageBounds.Right - rect.Right),
                Math.Clamp(delta.Y, -rect.Top, _imageBounds.Bottom - rect.Bottom));
        }
        else
        {
            var left = _drag.HasFlag(DragEdges.Left) ? Math.Clamp(rect.Left + delta.X, 0, rect.Right - 1) : rect.Left;
            var right = _drag.HasFlag(DragEdges.Right) ? Math.Clamp(rect.Right + delta.X, rect.Left + 1, _imageBounds.Right) : rect.Right;
            var top = _drag.HasFlag(DragEdges.Top) ? Math.Clamp(rect.Top + delta.Y, 0, rect.Bottom - 1) : rect.Top;
            var bottom = _drag.HasFlag(DragEdges.Bottom) ? Math.Clamp(rect.Bottom + delta.Y, rect.Top + 1, _imageBounds.Bottom) : rect.Bottom;
            rect = new Rect(new Point(left, top), new Point(right, bottom));
        }
        SetSelection(rect);
    }

    private void SetSelection(Rect rect)
    {
        var snapped = new Rect(new Point(Math.Round(rect.Left), Math.Round(rect.Top)), new Point(Math.Round(rect.Right), Math.Round(rect.Bottom)));
        if (snapped.Width < 1 || snapped.Height < 1 || snapped == _selection)
            return;

        _selection = snapped;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, _imageBounds.Right), Math.Clamp(point.Y, 0, _imageBounds.Bottom));

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [Flags]
    private enum DragEdges
    {
        New = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Move = 16,
        TopLeft = Left | Top, TopRight = Right | Top, BottomLeft = Left | Bottom, BottomRight = Right | Bottom
    }
}
