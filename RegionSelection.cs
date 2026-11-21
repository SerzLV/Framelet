using Drawing = System.Drawing;

namespace Framelet;

internal enum SelectionCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal sealed class RegionSelection
{
    private enum DragKind { Create, Resize, Move }
    internal const int DragThresholdDips = 6;
    private const int MinimumSize = 3;
    private static readonly SelectionCorner[] CornerOrder = Enum.GetValues<SelectionCorner>();
    private readonly IReadOnlyList<Drawing.Rectangle> _displays;
    private readonly IReadOnlyList<Drawing.Rectangle> _windows;
    private readonly bool _singleDisplay;
    private readonly Drawing.Rectangle _desktop;
    private Drawing.Rectangle _limits;
    private Drawing.Rectangle _dragLimits;
    private Drawing.Point? _anchor;
    private Drawing.Size _dragThreshold;
    private bool _dragStarted;
    private DragKind _dragKind;
    private Drawing.Rectangle _lastMove;

    internal RegionSelection(IReadOnlyList<Drawing.Rectangle> displays, IReadOnlyList<Drawing.Rectangle> windows,
        bool singleDisplay)
    {
        _displays = displays;
        _windows = windows;
        _singleDisplay = singleDisplay;
        _desktop = displays.Aggregate(Drawing.Rectangle.Union);
        _limits = _desktop;
    }

    internal event Action? Changed;
    internal static ReadOnlySpan<SelectionCorner> Corners => CornerOrder;
    internal Drawing.Point Pointer { get; private set; }
    internal Drawing.Rectangle? Selection { get; private set; }
    internal SelectionCorner? ResizeCorner { get; private set; }
    internal bool IsDragging => _anchor.HasValue;
    internal bool IsLocked { get; private set; }

    internal Drawing.Rectangle? Preview
    {
        get
        {
            if (_anchor is { } start)
            {
                if (_dragKind != DragKind.Resize && !_dragStarted)
                    return Selection ?? WindowAt(start);

                var draft = Draft(start, Pointer);
                return IsSelectable(draft) ? draft : Selection;
            }
            return Selection ?? WindowAt(Pointer);
        }
    }

    internal void MovePointer(Drawing.Point point)
    {
        if (Pointer == point)
            return;
        Pointer = point;
        if (_anchor is { } start)
        {
            _dragStarted |= IsDragDistance(start, point);
            if (_dragStarted && _dragKind == DragKind.Move)
                _lastMove = Draft(start, point);
        }
        Changed?.Invoke();
    }

    internal void Begin(Drawing.Point point, SelectionCorner? corner = null, Drawing.Size? dragThreshold = null)
    {
        if (IsLocked)
            return;
        var display = _displays.FirstOrDefault(bounds => bounds.Contains(point));
        if (display.IsEmpty)
            return;
        Pointer = point;
        ResizeCorner = Selection is not null ? corner : null;
        _dragKind = ResizeCorner.HasValue ? DragKind.Resize
            : Selection is { } frame && frame.Contains(point) ? DragKind.Move : DragKind.Create;
        _lastMove = Selection.GetValueOrDefault();
        _dragLimits = _dragKind != DragKind.Create ? _limits : _singleDisplay ? display : _desktop;
        _anchor = point;
        _dragThreshold = dragThreshold ?? new Drawing.Size(DragThresholdDips, DragThresholdDips);
        _dragStarted = false;
        Changed?.Invoke();
    }

    internal Drawing.Rectangle? End(Drawing.Point point)
    {
        if (_anchor is not { } start)
            return null;

        var dragging = _dragKind == DragKind.Resize || _dragStarted || IsDragDistance(start, point);
        var draft = dragging ? Draft(start, point) : default;
        Pointer = point;
        _anchor = null;
        ResizeCorner = null;
        if (!dragging)
        {
            if (Selection is { } selected && selected.Contains(start))
                return selected;
            Selection = WindowAt(start);
        }
        else if (IsSelectable(draft))
        {
            Selection = draft;
            _limits = _dragLimits;
        }
        Changed?.Invoke();
        return null;
    }

    internal void CancelDrag()
    {
        if (_anchor is null)
            return;
        _anchor = null;
        ResizeCorner = null;
        Changed?.Invoke();
    }

    internal void SetLocked(bool locked)
    {
        if (IsLocked == locked)
            return;
        IsLocked = locked;
        Changed?.Invoke();
    }

    internal SelectionCorner? CornerAt(Drawing.Point point, Drawing.Size hitRadius)
    {
        if (IsLocked || IsDragging || Selection is not { } region)
            return null;

        // Preserve a click target inside small selections.
        var xReach = Math.Min(hitRadius.Width, region.Width / 4);
        var yReach = Math.Min(hitRadius.Height, region.Height / 4);
        SelectionCorner? nearest = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var corner in Corners)
        {
            var center = CornerPosition(region, corner);
            var inwardX = IsLeft(corner) ? point.X - center.X : center.X - point.X;
            var inwardY = IsTop(corner) ? point.Y - center.Y : center.Y - point.Y;
            if (inwardX < -hitRadius.Width || inwardX > xReach || inwardY < -hitRadius.Height || inwardY > yReach)
                continue;

            var distance = (double)inwardX * inwardX + (double)inwardY * inwardY;
            if (distance < bestDistance)
            {
                nearest = corner;
                bestDistance = distance;
            }
        }
        return nearest;
    }

    internal static Drawing.Point CornerPosition(Drawing.Rectangle region, SelectionCorner corner) =>
        new(IsLeft(corner) ? region.Left : region.Right, IsTop(corner) ? region.Top : region.Bottom);

    private Drawing.Rectangle Draft(Drawing.Point start, Drawing.Point point)
    {
        if (_dragKind == DragKind.Move && Selection is { } originFrame)
            return MoveWithinBounds(originFrame, point.X - start.X, point.Y - start.Y) ?? _lastMove;
        if (ResizeCorner is not { } corner || Selection is not { } region)
            return FromPoints(start, Clamp(point, _dragLimits));

        // Apply the pointer delta so grabbing slightly off-center does not jump the handle.
        var origin = CornerPosition(region, corner);
        var x = origin.X + point.X - start.X;
        var y = origin.Y + point.Y - start.Y;
        var left = IsLeft(corner);
        var top = IsTop(corner);
        return Drawing.Rectangle.FromLTRB(
            left ? Math.Clamp(x, _dragLimits.Left, region.Right - MinimumSize) : region.Left,
            top ? Math.Clamp(y, _dragLimits.Top, region.Bottom - MinimumSize) : region.Top,
            left ? region.Right : Math.Clamp(x, region.Left + MinimumSize, _dragLimits.Right),
            top ? region.Bottom : Math.Clamp(y, region.Top + MinimumSize, _dragLimits.Bottom));
    }

    private static bool IsLeft(SelectionCorner corner) => corner is SelectionCorner.TopLeft or SelectionCorner.BottomLeft;

    private static bool IsTop(SelectionCorner corner) => corner is SelectionCorner.TopLeft or SelectionCorner.TopRight;

    internal void Nudge(int x, int y)
    {
        if (IsLocked || IsDragging || Selection is not { } region)
            return;

        if (MoveWithinBounds(region, x, y) is not { } moved || moved == region)
            return;
        Selection = moved;
        Changed?.Invoke();
    }

    private Drawing.Rectangle? MoveWithinBounds(Drawing.Rectangle region, int x, int y)
    {
        region.X = Math.Clamp(region.X + x, _limits.Left, _limits.Right - region.Width);
        region.Y = Math.Clamp(region.Y + y, _limits.Top, _limits.Bottom - region.Height);
        return _displays.Any(display => display.IntersectsWith(region)) ? region : null;
    }

    private Drawing.Rectangle? WindowAt(Drawing.Point point)
    {
        if (_singleDisplay)
            return null;
        foreach (var window in _windows)
        {
            if (!window.Contains(point))
                continue;
            var clipped = Drawing.Rectangle.Intersect(window, _desktop);
            if (IsSelectable(clipped))
                return clipped;
        }
        return null;
    }

    private bool IsSelectable(Drawing.Rectangle rect) =>
        rect.Width >= MinimumSize && rect.Height >= MinimumSize && _displays.Any(display => display.IntersectsWith(rect));

    private bool IsDragDistance(Drawing.Point start, Drawing.Point end) =>
        Math.Abs(end.X - start.X) >= _dragThreshold.Width || Math.Abs(end.Y - start.Y) >= _dragThreshold.Height;

    private static Drawing.Point Clamp(Drawing.Point point, Drawing.Rectangle limits) =>
        new(Math.Clamp(point.X, limits.Left, limits.Right), Math.Clamp(point.Y, limits.Top, limits.Bottom));

    private static Drawing.Rectangle FromPoints(Drawing.Point start, Drawing.Point end) =>
        Drawing.Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
}
