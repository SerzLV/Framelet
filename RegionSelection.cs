using Drawing = System.Drawing;

namespace Framelet;

internal sealed class RegionSelection
{
    private const int MinimumSize = 3;
    private readonly IReadOnlyList<Drawing.Rectangle> _displays;
    private readonly IReadOnlyList<Drawing.Rectangle> _windows;
    private readonly bool _singleDisplay;
    private readonly Drawing.Rectangle _desktop;
    private Drawing.Rectangle _limits;
    private Drawing.Rectangle _dragLimits;
    private Drawing.Point? _anchor;
    private bool _dragMoved;

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
    internal Drawing.Point Pointer { get; private set; }
    internal Drawing.Rectangle? Selection { get; private set; }
    internal bool IsDragging => _anchor.HasValue;

    internal Drawing.Rectangle? Preview
    {
        get
        {
            if (_anchor is { } start)
            {
                var draft = FromPoints(start, Clamp(Pointer, _dragLimits));
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
        if (_anchor is { } start && IsDragDistance(start, point))
            _dragMoved = true;
        Changed?.Invoke();
    }

    internal void Begin(Drawing.Point point)
    {
        var display = _displays.FirstOrDefault(bounds => bounds.Contains(point));
        if (display.IsEmpty)
            return;
        Pointer = point;
        _dragLimits = _singleDisplay ? display : _desktop;
        _anchor = point;
        _dragMoved = false;
        Changed?.Invoke();
    }

    internal Drawing.Rectangle? End(Drawing.Point point)
    {
        if (_anchor is not { } start)
            return null;

        Pointer = point;
        _anchor = null;
        var draft = FromPoints(start, Clamp(point, _dragLimits));
        if (IsSelectable(draft))
        {
            Selection = draft;
            _limits = _dragLimits;
        }
        else if (!_dragMoved && !IsDragDistance(start, point))
        {
            if (Selection is { } selected && selected.Contains(start) && selected.Contains(point))
                return selected;
            Selection = WindowAt(point);
        }
        Changed?.Invoke();
        return null;
    }

    internal void CancelDrag()
    {
        if (_anchor is null)
            return;
        _anchor = null;
        Changed?.Invoke();
    }

    internal void Nudge(int x, int y)
    {
        if (IsDragging || Selection is not { } region)
            return;

        region.X = Math.Clamp(region.X + x, _limits.Left, _limits.Right - region.Width);
        region.Y = Math.Clamp(region.Y + y, _limits.Top, _limits.Bottom - region.Height);
        if (region == Selection || !_displays.Any(display => display.IntersectsWith(region)))
            return;
        Selection = region;
        Changed?.Invoke();
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

    private static bool IsDragDistance(Drawing.Point start, Drawing.Point end) =>
        Math.Abs(end.X - start.X) >= MinimumSize || Math.Abs(end.Y - start.Y) >= MinimumSize;

    private static Drawing.Point Clamp(Drawing.Point point, Drawing.Rectangle limits) =>
        new(Math.Clamp(point.X, limits.Left, limits.Right), Math.Clamp(point.Y, limits.Top, limits.Bottom));

    private static Drawing.Rectangle FromPoints(Drawing.Point start, Drawing.Point end) =>
        Drawing.Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
}
