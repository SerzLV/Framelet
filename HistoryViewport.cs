namespace Framelet;

public static class HistoryViewport
{
    public const double TileWidth = 231;
    public const double TileHeight = 170;

    internal static (int Start, int Count, double Before, double After) VisibleRange(
        int itemCount, double availableWidth, double viewportHeight, double top)
    {
        var columns = Math.Max(1, (int)(availableWidth / TileWidth));
        var rows = (int)Math.Ceiling((double)itemCount / columns);
        var firstRow = Math.Clamp((int)Math.Floor(-top / TileHeight) - 1, 0, rows);
        var endRow = Math.Clamp((int)Math.Ceiling((viewportHeight - top) / TileHeight) + 1, firstRow, rows);
        var firstItem = (int)Math.Min(itemCount, (long)firstRow * columns);
        var endItem = (int)Math.Min(itemCount, (long)endRow * columns);
        return (firstItem, endItem - firstItem, firstRow * TileHeight, (rows - endRow) * TileHeight);
    }
}
