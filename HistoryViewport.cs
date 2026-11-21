using System.Windows;

namespace Framelet;

public static class HistoryViewport
{
    public const double CardWidth = 221;
    public const double CardHeight = 160;
    public const double TileWidth = CardWidth + 10;
    public const double TileHeight = CardHeight + 10;

    internal static (int Start, int Count, double Before, double After) VisibleRange(
        int itemCount, double availableWidth, double viewportHeight, double top)
    {
        var columns = ColumnCount(availableWidth);
        var rows = (int)Math.Ceiling((double)itemCount / columns);
        var firstRow = Math.Clamp((int)Math.Floor(-top / TileHeight) - 1, 0, rows);
        var endRow = Math.Clamp((int)Math.Ceiling((viewportHeight - top) / TileHeight) + 1, firstRow, rows);
        var firstItem = (int)Math.Min(itemCount, (long)firstRow * columns);
        var endItem = (int)Math.Min(itemCount, (long)endRow * columns);
        return (firstItem, endItem - firstItem, firstRow * TileHeight, (rows - endRow) * TileHeight);
    }

    internal static IEnumerable<int> IntersectingCards(int itemCount, double availableWidth, Rect bounds)
    {
        if (itemCount == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            yield break;

        var columns = ColumnCount(availableWidth);
        var firstRow = Math.Max(0, (int)(bounds.Top / TileHeight));
        var lastRow = Math.Min((itemCount - 1) / columns, (int)(bounds.Bottom / TileHeight));
        var firstColumn = Math.Max(0, (int)(bounds.Left / TileWidth));
        var lastColumn = Math.Min(columns - 1, (int)(bounds.Right / TileWidth));
        for (var row = firstRow; row <= lastRow; row++)
        {
            var y = row * TileHeight;
            if (y + CardHeight <= bounds.Top || y >= bounds.Bottom)
                continue;

            var rowStart = row * columns;
            for (var column = firstColumn; column <= lastColumn && column < itemCount - rowStart; column++)
            {
                var x = column * TileWidth;
                if (x + CardWidth > bounds.Left && x < bounds.Right)
                    yield return rowStart + column;
            }
        }
    }

    private static int ColumnCount(double availableWidth) => Math.Max(1, (int)(availableWidth / TileWidth));
}
