using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Framelet;

internal sealed record EditorDocument(BitmapSource Image, EditJournal? Journal = null);

internal sealed record EditJournal(int Version, MarkState[] Marks, EditState[] Undo, EditState[] Redo, CropRegion? Crop = null)
{
    internal const int CurrentVersion = 2;
    internal const int SavedSteps = 10;

    internal bool IsUsable()
    {
        return Version is 1 or CurrentVersion && Marks is not null && Undo is not null && Redo is not null
            && Undo.Length <= SavedSteps && Redo.Length <= SavedSteps
            && (Crop?.HasArea ?? true) && (Version == CurrentVersion || Crop is null)
            && Marks.All(mark => mark is not null && mark.IsUsable())
            && CanReplay(Undo, backward: true)
            && CanReplay(Redo, backward: false);
    }

    private bool CanReplay(EditState[] edits, bool backward)
    {
        var count = Marks.Length;
        var crop = Crop;
        foreach (var edit in edits)
        {
            if (edit is null)
                return false;
            if (edit.Crop is { } change)
            {
                var prior = backward ? change.After : change.Before;
                var next = backward ? change.Before : change.After;
                if (Version != CurrentVersion || edit.Before is not null || edit.After is not null || crop != prior
                    || next == prior || next?.HasArea == false)
                    return false;

                crop = next;
                continue;
            }
            if ((edit.Before is null && edit.After is null)
                || edit.Before?.IsUsable() == false || edit.After?.IsUsable() == false)
                return false;

            var from = backward ? edit.After : edit.Before;
            var to = backward ? edit.Before : edit.After;
            if (edit.Index < 0 || edit.Index > count || (from is not null && edit.Index == count))
                return false;

            count += from is null ? 1 : to is null ? -1 : 0;
        }
        return true;
    }

    internal bool MatchesImage(int width, int height, int renderedWidth, int renderedHeight)
    {
        return (Crop?.Width ?? width) == renderedWidth && (Crop?.Height ?? height) == renderedHeight
            && Fits(Crop) && Undo.Concat(Redo).All(edit => edit.Crop is null || (Fits(edit.Crop.Before) && Fits(edit.Crop.After)));

        bool Fits(CropRegion? crop) => crop is null || crop.IsWithin(width, height);
    }
}

internal sealed record CropRegion(int X, int Y, int Width, int Height)
{
    internal bool HasArea => X >= 0 && Y >= 0 && Width > 0 && Height > 0;
    internal bool IsWithin(int width, int height) => HasArea && Width <= width - X && Height <= height - Y;
    internal Int32Rect ToPixels() => new(X, Y, Width, Height);
}

internal sealed record CropChange(CropRegion? Before, CropRegion? After);

internal sealed record MarkState(EditorTool Tool, uint Argb, double Size, MarkPoint[] Points, string? Label, bool BothHeads)
{
    internal bool IsUsable()
    {
        if (!double.IsFinite(Size) || Size is < 1 or > 12 || Points is null
            || !Points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)))
            return false;

        return Tool switch
        {
            EditorTool.Arrow or EditorTool.Line => Points.Length == 3,
            EditorTool.CurveArrow => Points.Length == 4,
            EditorTool.Rectangle or EditorTool.Ellipse => Points.Length == 2,
            EditorTool.Redact => Points.Length >= 2 && Points.Length % 2 == 0,
            EditorTool.Pen or EditorTool.Marker => Points.Length >= 1,
            EditorTool.Text or EditorTool.Number => Points.Length == 1 && !string.IsNullOrWhiteSpace(Label),
            _ => false
        };
    }

    internal static uint Pack(Color color)
        => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    internal Color ToColor() => Color.FromArgb((byte)(Argb >> 24), (byte)(Argb >> 16), (byte)(Argb >> 8), (byte)Argb);
}

internal readonly record struct MarkPoint(double X, double Y)
{
    internal Point ToPoint() => new(X, Y);
}

internal sealed record EditState(int Index, MarkState? Before, MarkState? After, CropChange? Crop = null);

