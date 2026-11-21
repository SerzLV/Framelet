using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Framelet;

public enum EditorTool
{
    Arrow,
    CurveArrow,
    Line,
    Rectangle,
    Ellipse,
    Pen,
    Marker,
    Text,
    Number,
    Redact,
    Select,
    Hand
}

public sealed partial class EditorSurface : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush RedactionBrush = (Brush)new SolidColorBrush(Color.FromRgb(16, 19, 24)).GetAsFrozen();
    private readonly List<Annotation> _marks = [];
    private readonly Stack<Edit> _undo = [];
    private readonly Stack<Edit> _redo = [];
    private BitmapSource? _image;
    private CropRegion? _crop;
    private Annotation? _draft;
    private int _selectedIndex = -1;
    private int _dragHandle = -1;
    private Point? _moveStart;
    private Rect _moveBounds;
    private Annotation? _strokeEditBefore;
    private int _strokeEditIndex;
    private EditorTool _tool;
    private double _zoom = 1;

    public EditorSurface()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                CommitTextInput();
        };
    }

    internal event Action? Changed;
    internal event Action? SelectionChanged;
    internal EditorTool Tool
    {
        get => _tool;
        set
        {
            CommitTextInput();
            CompleteStrokeAdjustment();
            CancelDraft();
            var oldTool = _tool;
            _tool = value;
            if (oldTool != value && value != EditorTool.Select)
                SelectAnnotation(-1);
            else
                Cursor = ToolCursor;
        }
    }
    private Annotation? SelectedMark => _selectedIndex < 0 ? null : _marks[_selectedIndex];
    private bool EditingMark => _dragHandle >= 0 || _moveStart is not null;
    internal Color Ink { get; private set; } = Color.FromRgb(211, 250, 133);
    internal double StrokeWidth { get; private set; } = 4;
    internal Color ActiveInk => SelectedMark?.Brush.Color ?? Ink;
    internal double ActiveStrokeWidth => SelectedMark?.Size ?? StrokeWidth;
    private Cursor ToolCursor => Tool switch
    {
        EditorTool.Hand => Cursors.Hand,
        EditorTool.Select => Cursors.Arrow,
        _ => Cursors.Cross
    };
    internal bool CanChangeAppearance => Tool is not (EditorTool.Redact or EditorTool.Hand) && SelectedMark?.Tool != EditorTool.Redact;
    internal bool ActiveSizeIsTextSize => (SelectedMark?.Tool ?? Tool) is EditorTool.Number or EditorTool.Text;
    internal bool ShowCurveOptions => Tool == EditorTool.CurveArrow || SelectedMark?.Tool == EditorTool.CurveArrow;
    internal bool NewCurveHasBothHeads { get; private set; }
    internal bool CurveHasBothHeads => _selectedIndex >= 0 && _marks[_selectedIndex].Tool == EditorTool.CurveArrow
        ? _marks[_selectedIndex].BothHeads
        : NewCurveHasBothHeads;
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal bool HasSelection => _selectedIndex >= 0;
    internal bool HasAnnotations => _marks.Count > 0;
    internal int OcrRevision { get; private set; }
    internal bool IsShowingSource(BitmapSource image) => ReferenceEquals(_image, image);

    internal static EditorTool? ToolForKey(Key key) => key switch
    {
        Key.S => EditorTool.Select,
        Key.H => EditorTool.Hand,
        Key.A => EditorTool.Arrow,
        Key.V => EditorTool.CurveArrow,
        Key.L => EditorTool.Line,
        Key.R => EditorTool.Rectangle,
        Key.O => EditorTool.Ellipse,
        Key.P => EditorTool.Pen,
        Key.M => EditorTool.Marker,
        Key.T => EditorTool.Text,
        Key.N => EditorTool.Number,
        Key.B => EditorTool.Redact,
        _ => null
    };
    private int ViewX => _crop?.X ?? 0;
    private int ViewY => _crop?.Y ?? 0;
    internal (int X, int Y) CropOffset => (ViewX, ViewY);

    internal void SetDrawingDefaults(Color ink, double width)
    {
        Ink = ink;
        StrokeWidth = width;
    }

    internal void SetInk(Color color)
    {
        CommitTextInput();
        CompleteStrokeAdjustment();
        Ink = color;
        if (CanChangeAppearance && SelectedMark is { } mark && mark.Brush.Color != color)
            Commit(new Edit(_selectedIndex, mark, mark with { Brush = new SolidColorBrush(color) }));
    }

    internal void SetStrokeWidth(double width)
    {
        CommitTextInput();
        StrokeWidth = width;
        if (!CanChangeAppearance || SelectedMark is not { } mark || mark.Size == width)
            return;

        var resized = mark with { Size = width };
        if (_strokeEditBefore is not null && _strokeEditIndex == _selectedIndex)
        {
            _marks[_selectedIndex] = resized;
            InvalidateVisual();
        }
        else
            Commit(new Edit(_selectedIndex, mark, resized));
    }

    internal void StartStrokeAdjustment()
    {
        CommitTextInput();
        if (CanChangeAppearance && SelectedMark is { } mark)
        {
            _strokeEditBefore = mark;
            _strokeEditIndex = _selectedIndex;
        }
    }

    internal void CompleteStrokeAdjustment()
    {
        if (_strokeEditBefore is not { } before)
            return;

        _strokeEditBefore = null;
        var after = _marks[_strokeEditIndex];
        if (after.Size != before.Size)
            Commit(new Edit(_strokeEditIndex, before, after));
    }

    internal void SetZoom(double zoom)
    {
        _zoom = zoom;
        InvalidateVisual();
    }

    internal void SetCurveHeads(bool both)
    {
        CancelDraft();
        NewCurveHasBothHeads = both;
        if (_selectedIndex >= 0 && _marks[_selectedIndex].Tool == EditorTool.CurveArrow && _marks[_selectedIndex].BothHeads != both)
        {
            var mark = _marks[_selectedIndex];
            Commit(new Edit(_selectedIndex, mark, mark with { BothHeads = both }));
        }
    }

    internal void Reset(BitmapSource image, EditJournal? journal = null)
    {
        FinishTextInput(save: false);
        CancelDraft();
        _image = image;
        _crop = journal?.Crop;
        Width = _crop?.Width ?? image.PixelWidth;
        Height = _crop?.Height ?? image.PixelHeight;
        ResetAnnotations();
        if (journal is not null)
        {
            _marks.AddRange(journal.Marks.Select(Annotation.FromState));
            RenumberMarkers();
            foreach (var edit in journal.Undo.Reverse())
                _undo.Push(Edit.FromState(edit));
            foreach (var edit in journal.Redo.Reverse())
                _redo.Push(Edit.FromState(edit));
        }
        InvalidateVisual();
        Changed?.Invoke();
    }

    internal void Clear()
    {
        FinishTextInput(save: false);
        CancelDraft();
        _image = null;
        _crop = null;
        ResetAnnotations();
        Width = Height = double.NaN;
    }

    private void ResetAnnotations()
    {
        _marks.Clear();
        _undo.Clear();
        _redo.Clear();
        _strokeEditBefore = null;
        OcrRevision++;
        SelectAnnotation(-1);
    }

    internal void Undo()
    {
        CommitTextInput();
        CompleteStrokeAdjustment();
        CancelDraft();
        if (!_undo.TryPop(out var edit))
            return;

        _redo.Push(edit);
        Apply(edit, forward: false);
    }

    internal void Redo()
    {
        CommitTextInput();
        CompleteStrokeAdjustment();
        CancelDraft();
        if (!_redo.TryPop(out var edit))
            return;

        _undo.Push(edit);
        Apply(edit, forward: true);
    }

    internal bool DeleteSelection()
    {
        CommitTextInput();
        CompleteStrokeAdjustment();
        CancelDraft();
        if (SelectedMark is not { } mark)
            return false;

        Commit(new Edit(_selectedIndex, mark, null));
        return true;
    }

    internal void RedactContacts(IReadOnlyList<Rect> regions)
    {
        CompleteStrokeAdjustment();
        CancelDraft();
        var points = new List<Point>(regions.Count * 2);
        foreach (var region in regions)
        {
            points.Add(ImagePoint(region.TopLeft));
            points.Add(ImagePoint(region.BottomRight));
        }
        if (points.Count > 0)
            Add(new Annotation(EditorTool.Redact, new SolidColorBrush(Ink), StrokeWidth, points));
    }

    internal bool Crop(Int32Rect selection)
    {
        var region = new CropRegion(selection.X, selection.Y, selection.Width, selection.Height);
        if (_image is null || !region.IsWithin((int)Width, (int)Height)
            || (region.Width == Width && region.Height == Height))
            return false;

        CompleteStrokeAdjustment();
        CancelDraft();
        var absolute = region with { X = ViewX + region.X, Y = ViewY + region.Y };
        Commit(new Edit(-1, null, null, new CropChange(_crop, absolute)));
        return true;
    }

    internal EditorDocument? CaptureJournal()
    {
        if (_image is null || (_crop is null && _marks.Count == 0 && !CanUndo && !CanRedo))
            return null;

        return new EditorDocument(_image, new EditJournal(EditJournal.CurrentVersion,
            _marks.Select(mark => mark.ToState()).ToArray(),
            _undo.Take(EditJournal.SavedSteps).Select(edit => edit.ToState()).ToArray(),
            _redo.Take(EditJournal.SavedSteps).Select(edit => edit.ToState()).ToArray(), _crop));
    }

    internal void CancelDraft()
    {
        _draft = null;
        _dragHandle = -1;
        _moveStart = null;
        Cursor = ToolCursor;
        if (IsMouseCaptured)
            ReleaseMouseCapture();

        InvalidateVisual();
    }

    internal Task<BitmapSource> ExportAsync()
    {
        CompleteStrokeAdjustment();
        return ExportSnapshotAsync(redactionsOnly: false);
    }

    internal Task<BitmapSource> ExportForOcrAsync() => ExportSnapshotAsync(redactionsOnly: true);

    private Task<BitmapSource> ExportSnapshotAsync(bool redactionsOnly)
    {
        var image = _image ?? throw new UserFacingException("OpenFirst");
        var crop = _crop;
        var marks = _marks.Where(mark => !redactionsOnly || mark.Tool == EditorTool.Redact)
            .Select(mark => mark.ToState()).ToArray();
        if (!image.IsFrozen)
            image = (BitmapSource)image.GetAsFrozen();
        if (marks.Length == 0 && crop is null)
            return Task.FromResult(image);

        return Task.Run(() => Rasterize(image, crop, marks));
    }

    private static BitmapSource Rasterize(BitmapSource image, CropRegion? crop, MarkState[] marks)
    {
        if (marks.Length == 0)
            return CaptureService.Crop(image, crop!.ToPixels());

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            DrawBase(drawing, image, crop);
            foreach (var mark in marks)
                DrawMark(drawing, Annotation.FromState(mark));
            drawing.Pop();
            drawing.Pop();
        }

        var bitmap = new RenderTargetBitmap(crop?.Width ?? image.PixelWidth, crop?.Height ?? image.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (_image is not null)
            DrawPreview(drawing, _image);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_image is null || Tool == EditorTool.Hand)
            return;

        Focus();
        CommitTextInput();
        CompleteStrokeAdjustment();
        var start = ImagePoint(e.GetPosition(this));
        if (Tool == EditorTool.Select)
        {
            if (e.ClickCount == 2 && FindAnnotation(start) is var index && index >= 0 && _marks[index].Tool == EditorTool.Text)
            {
                SelectAnnotation(index);
                EditSelectedText();
            }
            else if (!TryDragHandle(start))
                StartMovingAnnotation(start);
            e.Handled = true;
            return;
        }
        if (HasEditablePoints(Tool) && (TryDragHandle(start) || TrySelectPath(start)))
        {
            e.Handled = true;
            return;
        }
        SelectAnnotation(-1);
        var mark = new Annotation(Tool, new SolidColorBrush(Ink), StrokeWidth, [start]);
        switch (Tool)
        {
            case EditorTool.Text:
                BeginTextInput(mark);
                break;
            case EditorTool.Number:
                var number = _marks.Count(entry => entry.Tool == EditorTool.Number) + 1;
                Add(mark with { Label = number.ToString(CultureInfo.InvariantCulture) });
                break;
            default:
                if (CaptureMouse())
                {
                    _draft = Tool switch
                    {
                        EditorTool.CurveArrow => mark with { Points = [start, start, start, start], BothHeads = NewCurveHasBothHeads },
                        EditorTool.Arrow or EditorTool.Line => mark with { Points = [start, start, start] },
                        _ => mark with { Points = [start, start] }
                    };
                    InvalidateVisual();
                }
                break;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draft is not null)
            MoveDraft(ImagePoint(e.GetPosition(this)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_draft is null)
            return;

        FinishDraft(ImagePoint(e.GetPosition(this)));
        e.Handled = true;
    }

    private void FinishDraft(Point position)
    {
        var mark = _draft!;
        MoveDraft(position);
        var editing = EditingMark;
        CancelDraft();
        if (editing)
        {
            var previous = _marks[_selectedIndex];
            if (!previous.Points.SequenceEqual(mark.Points))
                Commit(new Edit(_selectedIndex, previous, mark));
        }
        else if (mark.Tool is EditorTool.Pen or EditorTool.Marker || (mark.Points[^1] - mark.Points[0]).Length >= 2)
            Add(mark);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        CancelDraft();
        SelectAnnotation(-1);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _draft = null;
        _dragHandle = -1;
        _moveStart = null;
        Cursor = ToolCursor;
        InvalidateVisual();
    }

    private static void DrawBase(DrawingContext drawing, BitmapSource image, CropRegion? crop)
    {
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, crop?.Width ?? image.PixelWidth, crop?.Height ?? image.PixelHeight)));
        drawing.PushTransform(new TranslateTransform(-(crop?.X ?? 0), -(crop?.Y ?? 0)));
        drawing.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
    }

    private void DrawPreview(DrawingContext drawing, BitmapSource image)
    {
        DrawBase(drawing, image, _crop);
        for (var index = 0; index < _marks.Count; index++)
            if (index != _textInput?.Index)
                DrawMark(drawing, EditingMark && index == _selectedIndex ? _draft! : _marks[index]);
        if (_draft is not null && !EditingMark)
            DrawMark(drawing, _draft);
        if (SelectedMark is { } selected && _textInput is null)
        {
            var mark = EditingMark ? _draft! : selected;
            if (HasEditablePoints(mark.Tool))
                DrawPathHandles(drawing, mark);
            else
                DrawSelectionCorners(drawing, MarkVisual(mark).ContentBounds);
        }
        drawing.Pop();
        drawing.Pop();
    }

    private void Add(Annotation mark) => Commit(new Edit(_marks.Count, null, mark));

    private void Commit(Edit edit)
    {
        _undo.Push(edit);
        _redo.Clear();
        Apply(edit, forward: true);
    }

    private void Apply(Edit edit, bool forward)
    {
        if (edit.Crop is { } crop)
        {
            _crop = forward ? crop.After : crop.Before;
            Width = _crop?.Width ?? _image!.PixelWidth;
            Height = _crop?.Height ?? _image!.PixelHeight;
            OcrRevision++;
            SelectAnnotation(-1);
            Changed?.Invoke();
            return;
        }
        var before = forward ? edit.Before : edit.After;
        var mark = forward ? edit.After : edit.Before;
        if (mark is null)
            _marks.RemoveAt(edit.Index);
        else if (before is null)
            _marks.Insert(edit.Index, mark);
        else
            _marks[edit.Index] = mark;

        if (before?.Tool == EditorTool.Number || mark?.Tool == EditorTool.Number)
            RenumberMarkers();
        if (edit.After?.Tool == EditorTool.Redact || edit.Before?.Tool == EditorTool.Redact)
            OcrRevision++;
        SelectAnnotation(mark is not null && (Tool == EditorTool.Select || mark.Tool == Tool) ? edit.Index : -1);
        InvalidateVisual();
        Changed?.Invoke();
    }

    private void RenumberMarkers()
    {
        var number = 0;
        for (var index = 0; index < _marks.Count; index++)
        {
            var mark = _marks[index];
            if (mark.Tool != EditorTool.Number)
                continue;

            var label = (++number).ToString(CultureInfo.InvariantCulture);
            if (mark.Label != label)
                _marks[index] = mark with { Label = label };
        }
    }

    private void SelectAnnotation(int index)
    {
        _selectedIndex = index;
        Cursor = ToolCursor;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    internal bool DeselectAnnotation()
    {
        if (_selectedIndex < 0)
            return false;

        SelectAnnotation(-1);
        return true;
    }

    private bool TryDragHandle(Point position)
    {
        if (SelectedMark is { } selected && HasEditablePoints(selected.Tool))
        {
            for (var handle = 0; handle < selected.Points.Count; handle++)
            {
                if ((position - selected.Points[handle]).Length > 9 / _zoom)
                    continue;

                if (CaptureMouse())
                {
                    _dragHandle = handle;
                    _draft = selected with { Points = [.. selected.Points] };
                    InvalidateVisual();
                }
                return true;
            }
        }
        return false;
    }

    private bool TrySelectPath(Point position)
    {
        for (var index = _marks.Count - 1; index >= 0; index--)
        {
            var mark = _marks[index];
            if (mark.Tool != Tool)
                continue;

            if (PathHit(mark, position))
            {
                SelectAnnotation(index);
                return true;
            }
        }

        return false;
    }

    private bool PathHit(Annotation mark, Point position)
    {
        var hitPen = MakePen(Brushes.Black, mark.Size + 14 / _zoom);
        var geometry = mark.Tool == EditorTool.CurveArrow
            ? CurveGeometry(mark.Points)
            : BentLineGeometry(mark.Points[0], mark.Points[1], mark.Points[2], mark.Size);
        return geometry.StrokeContains(hitPen, position);
    }

    private void StartMovingAnnotation(Point position)
    {
        SelectAnnotation(FindAnnotation(position));
        if (SelectedMark is not { } mark || !CaptureMouse())
            return;

        _moveStart = position;
        _moveBounds = MarkVisual(mark).ContentBounds;
        foreach (var point in mark.Points)
            _moveBounds.Union(point);
        _draft = mark with { Points = [.. mark.Points] };
        Cursor = Cursors.SizeAll;
    }

    private int FindAnnotation(Point position)
    {
        var inside = -1;
        for (var index = _marks.Count - 1; index >= 0; index--)
        {
            var mark = _marks[index];
            var visual = MarkVisual(mark);
            if (VisualTreeHelper.HitTest(visual, position) is not null || HasEditablePoints(mark.Tool) && PathHit(mark, position))
                return index;

            if (inside < 0 && IsInside(mark, position, visual.ContentBounds))
                inside = index;
        }
        return inside;
    }

    private static bool IsInside(Annotation mark, Point position, Rect inkBounds)
    {
        return mark.Tool switch
        {
            EditorTool.Ellipse => new EllipseGeometry(new Rect(mark.Points[0], mark.Points[^1])).FillContains(position),
            EditorTool.Rectangle or EditorTool.Text or EditorTool.Number => inkBounds.Contains(position),
            _ => false
        };
    }

    private static DrawingVisual MarkVisual(Annotation mark)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            DrawMark(drawing, mark);
        return visual;
    }

    private static bool HasEditablePoints(EditorTool tool) => tool is EditorTool.Arrow or EditorTool.Line or EditorTool.CurveArrow;

    private void MoveDraft(Point position)
    {
        var mark = _draft!;
        if (_moveStart is { } start)
            MoveAnnotation(mark, position - start);
        else if (mark.Tool is EditorTool.Pen or EditorTool.Marker)
        {
            if ((position - mark.Points[^1]).Length >= 0.7)
                mark.Points.Add(position);
        }
        else if (HasEditablePoints(mark.Tool))
        {
            if (_dragHandle >= 0)
                mark.Points[_dragHandle] = position;
            else if (mark.Tool == EditorTool.CurveArrow)
                SetCurvePoints(mark.Points, position);
            else
            {
                mark.Points[1] = mark.Points[0] + (position - mark.Points[0]) * .5;
                mark.Points[2] = position;
            }
        }
        else if (mark.Tool == EditorTool.Ellipse && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            mark.Points[^1] = CircleEnd(mark.Points[0], position);
        else
        {
            mark.Points[^1] = position;
        }
        InvalidateVisual();
    }

    private void MoveAnnotation(Annotation draft, Vector delta)
    {
        delta.X = Math.Clamp(delta.X, -Math.Max(0, _moveBounds.Left), Math.Max(0, _image!.PixelWidth - _moveBounds.Right));
        delta.Y = Math.Clamp(delta.Y, -Math.Max(0, _moveBounds.Top), Math.Max(0, _image.PixelHeight - _moveBounds.Bottom));
        var original = _marks[_selectedIndex].Points;
        for (var index = 0; index < original.Count; index++)
            draft.Points[index] = original[index] + delta;
    }

    private Point CircleEnd(Point start, Point end)
    {
        var direction = end - start;
        var xSign = direction.X < 0 ? -1 : 1;
        var ySign = direction.Y < 0 ? -1 : 1;
        var maxWidth = xSign < 0 ? start.X - ViewX : ViewX + Width - start.X;
        var maxHeight = ySign < 0 ? start.Y - ViewY : ViewY + Height - start.Y;
        var side = Math.Min(Math.Max(Math.Abs(direction.X), Math.Abs(direction.Y)), Math.Min(maxWidth, maxHeight));
        return new Point(start.X + side * xSign, start.Y + side * ySign);
    }

    private void SetCurvePoints(List<Point> points, Point end)
    {
        var start = points[0];
        var chord = end - start;
        var bend = chord.Length > 0 ? Math.Min(.72, 250 / chord.Length) : 0;
        var offset = new Vector(-chord.Y, chord.X) * bend;
        points[1] = Limit(start + chord * .30 + offset);
        points[2] = Limit(start + chord * .70 - offset);
        points[3] = end;
    }

    private Point Limit(Point point)
    {
        return new Point(Math.Clamp(point.X, ViewX, ViewX + Width), Math.Clamp(point.Y, ViewY, ViewY + Height));
    }

    private Point ImagePoint(Point viewport) => Limit(viewport + new Vector(ViewX, ViewY));

    private static void DrawMark(DrawingContext drawing, Annotation mark)
    {
        var start = mark.Points[0];
        var end = mark.Points[^1];
        switch (mark.Tool)
        {
            case EditorTool.Arrow:
                DrawArrow(drawing, mark, start, end);
                break;
            case EditorTool.Line:
                drawing.DrawGeometry(null, MakePen(mark.Brush, mark.Size), BentLineGeometry(start, mark.Points[1], end, mark.Size));
                break;
            case EditorTool.CurveArrow:
                DrawCurve(drawing, mark);
                break;
            case EditorTool.Rectangle:
                drawing.DrawRectangle(null, MakePen(mark.Brush, mark.Size), new Rect(start, end));
                break;
            case EditorTool.Ellipse:
                drawing.DrawGeometry(null, MakePen(mark.Brush, mark.Size), new EllipseGeometry(new Rect(start, end)));
                break;
            case EditorTool.Pen:
                DrawStroke(drawing, mark, mark.Size);
                break;
            case EditorTool.Marker:
                drawing.PushOpacity(.41);
                DrawStroke(drawing, mark, mark.Size * 3.8);
                drawing.Pop();
                break;
            case EditorTool.Text:
                DrawAnnotationText(drawing, mark, start);
                break;
            case EditorTool.Number:
                DrawNumber(drawing, mark, start);
                break;
            case EditorTool.Redact:
                for (var index = 0; index < mark.Points.Count; index += 2)
                {
                    var area = new Rect(mark.Points[index], mark.Points[index + 1]);
                    drawing.DrawRectangle(RedactionBrush, null,
                        new Rect(new Point(Math.Floor(area.Left), Math.Floor(area.Top)),
                            new Point(Math.Ceiling(area.Right), Math.Ceiling(area.Bottom))));
                }
                break;
        }
    }

    private static void DrawStroke(DrawingContext drawing, Annotation mark, double thickness)
    {
        if (mark.Points.Count <= 2 && mark.Points[0] == mark.Points[^1])
        {
            drawing.DrawEllipse(mark.Brush, null, mark.Points[0], thickness / 2, thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(mark.Points[0], false, false);
            path.PolyLineTo(mark.Points, true, true);
        }
        drawing.DrawGeometry(null, MakePen(mark.Brush, thickness), geometry);
    }

    private static void DrawArrow(DrawingContext drawing, Annotation mark, Point start, Point end)
    {
        var bend = mark.Points[1];
        if ((end - bend).Length < 1)
            bend = start;
        var direction = end - bend;
        var length = direction.Length;
        if (length < 1)
            return;

        direction.Normalize();
        var head = Math.Min(length * .86, Math.Max(9, mark.Size * 3.4));
        var basePoint = end - direction * head;
        var perpendicular = new Vector(-direction.Y, direction.X) * head * .51;
        drawing.DrawGeometry(null, MakePen(mark.Brush, mark.Size),
            BentLineGeometry(start, bend, end - direction * head * .6, mark.Size));
        var tip = new StreamGeometry();
        using (var path = tip.Open())
        {
            path.BeginFigure(end, true, true);
            path.LineTo(basePoint + perpendicular, true, false);
            path.LineTo(basePoint - perpendicular, true, false);
        }
        drawing.DrawGeometry(mark.Brush, null, tip);
    }

    private static void DrawNumber(DrawingContext drawing, Annotation mark, Point center)
    {
        var pixel = mark.Brush.Color;
        var lightness = .2126 * pixel.R + .7152 * pixel.G + .0722 * pixel.B;
        var radius = Math.Max(11, mark.Size * 3.8);
        var label = FormatLabel(mark.Label!, radius * 1.12, lightness > 140 ? Brushes.Black : Brushes.White);
        drawing.DrawEllipse(mark.Brush, MakePen(Brushes.Black, 1), center, radius, radius);
        drawing.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
    }

    private static StreamGeometry CurveGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], false, false);
            path.BezierTo(points[1], points[2], points[3], true, true);
        }
        return geometry;
    }

    private static StreamGeometry BentLineGeometry(Point start, Point bend, Point end, double thickness)
    {
        var incoming = bend - start;
        var outgoing = end - bend;
        var available = Math.Min(incoming.Length, outgoing.Length);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(start, false, false);
            if (available >= 1)
            {
                incoming.Normalize();
                outgoing.Normalize();
                if (Vector.Multiply(incoming, outgoing) < .999)
                {
                    var trim = Math.Min(available * .48, Math.Clamp(available * .32, 18 + thickness * 2, 84));
                    path.LineTo(bend - incoming * trim, true, true);
                    path.QuadraticBezierTo(bend, bend + outgoing * trim, true, true);
                }
            }
            path.LineTo(end, true, true);
        }
        return geometry;
    }

    private static void DrawCurve(DrawingContext drawing, Annotation mark)
    {
        var points = mark.Points;
        var pen = MakePen(mark.Brush, mark.Size);
        drawing.DrawGeometry(null, pen, CurveGeometry(points));
        var length = (points[1] - points[0]).Length + (points[2] - points[1]).Length + (points[3] - points[2]).Length;
        var headSize = Math.Min(length * .28, Math.Max(9, mark.Size * 3.4));
        DrawCurveHead(drawing, pen, points[3], points[3] - points[2], points[3] - points[0], headSize);
        if (mark.BothHeads)
            DrawCurveHead(drawing, pen, points[0], points[0] - points[1], points[0] - points[3], headSize);
    }

    private static void DrawCurveHead(DrawingContext drawing, Pen pen, Point tip, Vector tangent, Vector chord, double size)
    {
        if (tangent.Length < .5)
            tangent = chord;
        if (tangent.Length < .5 || size < 1)
            return;

        tangent.Normalize();
        var center = tip - tangent * size;
        var side = new Vector(-tangent.Y, tangent.X) * size * .58;
        var head = new StreamGeometry();
        using (var path = head.Open())
        {
            path.BeginFigure(center + side, false, false);
            path.LineTo(tip, true, true);
            path.LineTo(center - side, true, true);
        }
        drawing.DrawGeometry(null, pen, head);
    }

    private void DrawPathHandles(DrawingContext drawing, Annotation mark)
    {
        var unit = 1 / _zoom;
        var outline = MakePen(RedactionBrush, 1.45 * unit);
        var points = mark.Points;
        for (var i = 0; i < points.Count; i++)
        {
            var radius = 4.6 * unit;
            drawing.DrawEllipse(Brushes.White, outline, points[i], radius, radius);
            if (i > 0 && i < points.Count - 1)
                drawing.DrawEllipse(mark.Brush, null, points[i], 2.7 * unit, 2.7 * unit);
        }
    }

    private void DrawSelectionCorners(DrawingContext drawing, Rect bounds)
    {
        if (bounds.IsEmpty)
            return;

        var unit = 1 / _zoom;
        bounds.Inflate(4 * unit, 4 * unit);
        var arm = Math.Min(8 * unit, Math.Min(bounds.Width, bounds.Height) / 3);
        var pen = MakePen(Brushes.DeepSkyBlue, 1.4 * unit);
        DrawCorner(bounds.TopLeft, 1, 1);
        DrawCorner(bounds.TopRight, -1, 1);
        DrawCorner(bounds.BottomLeft, 1, -1);
        DrawCorner(bounds.BottomRight, -1, -1);

        void DrawCorner(Point corner, int horizontal, int vertical)
        {
            drawing.DrawLine(pen, corner, corner + new Vector(arm * horizontal, 0));
            drawing.DrawLine(pen, corner, corner + new Vector(0, arm * vertical));
        }
    }

    private static FormattedText FormatLabel(string label, double size, Brush brush)
    {
        return new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelTypeface, size, brush, 1);
    }

    private static Pen MakePen(Brush brush, double size)
    {
        return new Pen(brush, size)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
    }

    private sealed record Annotation(EditorTool Tool, SolidColorBrush Brush, double Size, List<Point> Points,
        string? Label = null, bool BothHeads = false)
    {
        internal MarkState ToState() => new(Tool, MarkState.Pack(Brush.Color), Size,
            Points.Select(point => new MarkPoint(point.X, point.Y)).ToArray(), Label, BothHeads);

        internal static Annotation FromState(MarkState state) => new(state.Tool, new SolidColorBrush(state.ToColor()),
            state.Size, state.Points.Select(point => point.ToPoint()).ToList(), state.Label, state.BothHeads);
    }

    private sealed record Edit(int Index, Annotation? Before, Annotation? After, CropChange? Crop = null)
    {
        internal EditState ToState() => new(Index, Before?.ToState(), After?.ToState(), Crop);

        internal static Edit FromState(EditState state) => new(state.Index,
            state.Before is { } before ? Annotation.FromState(before) : null,
            state.After is { } after ? Annotation.FromState(after) : null, state.Crop);
    }
}


