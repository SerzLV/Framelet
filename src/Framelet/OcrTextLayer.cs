using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Framelet;

internal sealed record OcrWord(string Text, Rect Bounds, int Line, int Offset);

internal sealed record OcrLayout(string RawText, IReadOnlyList<OcrWord> Words)
{
    internal string GetText(int first, int last)
    {
        if (first == 0 && last == Words.Count - 1)
            return OcrService.NormalizeShortcuts(RawText);

        var begin = Words[first];
        var end = Words[last];
        if (begin.Offset >= 0 && end.Offset >= begin.Offset)
            return OcrService.NormalizeShortcuts(RawText[begin.Offset..(end.Offset + end.Text.Length)]);

        var text = new StringBuilder(begin.Text);
        for (var index = first + 1; index <= last; index++)
        {
            var word = Words[index];
            text.Append(word.Line == Words[index - 1].Line ? " " : "\n");
            text.Append(word.Text);
        }
        return OcrService.NormalizeShortcuts(text.ToString());
    }
}

public sealed class OcrTextLayer : FrameworkElement
{
    private static readonly Brush SelectionFill = MakeBrush(Color.FromArgb(97, 190, 249, 122));
    private static readonly Brush SelectionEdge = MakeBrush(Color.FromArgb(225, 86, 151, 39));
    private static readonly Brush HoverFill = MakeBrush(Color.FromArgb(45, 199, 254, 142));
    private static readonly Brush FieldFill = MakeBrush(Color.FromArgb(64, 255, 204, 115));
    private static readonly Brush FieldEdge = MakeBrush(Color.FromRgb(221, 143, 44));
    private OcrLayout? _layout;
    private IReadOnlyList<Rect> _highlightedRegions = [];
    private int _anchor = -1;
    private int _end = -1;
    private int _hover = -1;
    private double _zoom = 1;
    private Point? _pendingStart;
    private Point _pendingEnd;
    private bool _selectAllWhenReady;

    public OcrTextLayer()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Arrow;
    }

    internal event Action? SelectionChanged;
    internal bool HasSelection => _anchor >= 0;
    internal bool HasPendingSelection => _pendingStart is not null || _selectAllWhenReady;
    internal bool HasWords => _layout?.Words.Count > 0;
    internal bool IsActive { get; private set; }
    internal string SelectedText => HasSelection && _layout is not null
        ? _layout.GetText(Math.Min(_anchor, _end), Math.Max(_anchor, _end)) : "";

    internal void SetLayout(OcrLayout? layout)
    {
        _layout = layout;
        SetHighlightedRegions([]);
        if (!HasWords)
        {
            ClearSelection();
        }
        else if (_selectAllWhenReady)
        {
            SetSelection(0, layout!.Words.Count - 1);
        }
        else if (_pendingStart is { } start)
        {
            var first = FindWord(start, nearest: false);
            if (first < 0 && start != _pendingEnd && FindWord(_pendingEnd, nearest: false) >= 0)
                first = FindWord(start, nearest: true);
            if (first >= 0)
                SetSelection(first, FindWord(_pendingEnd, nearest: true));
        }
        _pendingStart = null;
        _selectAllWhenReady = false;
        RefreshCursor();
    }

    internal void SetZoom(double zoom)
    {
        _zoom = zoom;
        InvalidateVisual();
    }

    internal void SetActive(bool active)
    {
        IsActive = IsHitTestVisible = Focusable = active;
        UpdateVisibility();
    }

    internal void SetHighlightedRegions(IReadOnlyList<Rect> regions)
    {
        _highlightedRegions = regions;
        UpdateVisibility();
        InvalidateVisual();
    }

    private void UpdateVisibility() => Visibility = IsActive || _highlightedRegions.Count > 0
        ? Visibility.Visible : Visibility.Collapsed;

    internal void SelectAll()
    {
        if (HasWords)
            SetSelection(0, _layout!.Words.Count - 1);
        else
            _selectAllWhenReady = true;
    }

    internal void ClearSelection()
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        _pendingStart = null;
        _selectAllWhenReady = false;
        SetHover(-1);
        SetSelection(-1, -1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        BeginSelection(e.GetPosition(this));
        CaptureMouse();
        e.Handled = true;
    }

    private void BeginSelection(Point position)
    {
        _selectAllWhenReady = false;
        if (HasWords)
        {
            var word = FindWord(position, nearest: false);
            SetSelection(word, word);
        }
        else
        {
            _pendingStart = position;
            _pendingEnd = position;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (IsMouseCaptured)
            ExtendSelection(position);
        else
            SetHover(FindWord(position, nearest: false));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ExtendSelection(e.GetPosition(this));
            ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    private void ExtendSelection(Point position)
    {
        if (_pendingStart is not null)
        {
            _pendingEnd = position;
            return;
        }

        if (_anchor >= 0)
            SetSelection(_anchor, FindWord(position, nearest: true));
        else
        {
            var word = FindWord(position, nearest: false);
            SetSelection(word, word);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e) => SetHover(-1);

    protected override void OnRender(DrawingContext drawing)
    {
        if (_highlightedRegions.Count > 0)
        {
            var edge = new Pen(FieldEdge, 1.4 / _zoom);
            foreach (var bounds in _highlightedRegions)
                DrawHighlight(drawing, bounds, FieldFill, edge);
        }
        if (!IsActive)
            return;

        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_layout is not { } layout)
            return;

        if (_hover >= 0 && !IsMouseCaptured)
            DrawHighlight(drawing, layout.Words[_hover].Bounds, HoverFill, null);
        if (_anchor < 0)
            return;

        var first = Math.Min(_anchor, _end);
        var last = Math.Max(_anchor, _end);
        var pen = new Pen(SelectionEdge, .8 / _zoom);
        var band = layout.Words[first].Bounds;
        for (var index = first + 1; index <= last; index++)
        {
            var next = layout.Words[index];
            var gap = Math.Max(0, Math.Max(band.Left - next.Bounds.Right, next.Bounds.Left - band.Right));
            if (next.Line == layout.Words[index - 1].Line && gap <= Math.Max(band.Height, next.Bounds.Height))
                band.Union(next.Bounds);
            else
            {
                DrawHighlight(drawing, band, SelectionFill, pen);
                band = next.Bounds;
            }
        }
        DrawHighlight(drawing, band, SelectionFill, pen);
    }

    private void DrawHighlight(DrawingContext drawing, Rect bounds, Brush fill, Pen? pen)
    {
        var padding = 1.5 / _zoom;
        bounds.Inflate(padding, padding);
        drawing.DrawRoundedRectangle(fill, pen, bounds, 2.2 / _zoom, 2.2 / _zoom);
    }

    private int FindWord(Point point, bool nearest)
    {
        if (_layout is not { } layout)
            return -1;

        var closest = -1;
        var distance = double.PositiveInfinity;
        for (var index = 0; index < layout.Words.Count; index++)
        {
            var bounds = layout.Words[index].Bounds;
            var x = Math.Max(0, Math.Max(bounds.Left - point.X, point.X - bounds.Right));
            var y = Math.Max(0, Math.Max(bounds.Top - point.Y, point.Y - bounds.Bottom));
            var score = x * x + 2 * y * y;
            var hitsLine = x <= 20 / _zoom && y <= 2 / _zoom;
            var hitsWord = x <= 9 / _zoom && y <= 6 / _zoom;
            if (score < distance && (nearest || hitsLine || hitsWord))
            {
                closest = index;
                distance = score;
                if (score == 0)
                    break;
            }
        }
        return closest;
    }

    private void SetSelection(int anchor, int end)
    {
        if (anchor == _anchor && end == _end)
            return;

        _anchor = anchor;
        _end = end;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private void SetHover(int word)
    {
        if (_hover == word)
            return;

        _hover = word;
        RefreshCursor();
        InvalidateVisual();
    }

    private void RefreshCursor() => Cursor = !HasWords ? Cursors.AppStarting
        : _hover >= 0 ? Cursors.IBeam : Cursors.Arrow;

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
