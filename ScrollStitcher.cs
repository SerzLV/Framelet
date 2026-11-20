using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Framelet;

internal enum ScrollFrameResult
{
    Added,
    Unchanged,
    NoOverlap,
    Limit
}

internal sealed class ScrollStitcher
{
    private const int MaximumHeight = 32_000;
    private const int MaximumPixels = 40_000_000;
    private readonly int _width;
    private readonly int _frameHeight;
    private readonly int _stride;
    private readonly List<byte[]> _parts = [];
    private ulong[][] _previousRows;
    private byte[]? _frameBuffer;

    internal ScrollStitcher(BitmapSource first)
    {
        _width = first.PixelWidth;
        _frameHeight = first.PixelHeight;
        _stride = checked(_width * 4);
        Height = _frameHeight;
        var pixels = AllocateFrame();
        CopyPixels(first, pixels);
        _parts.Add(pixels);
        _previousRows = ReadRows(pixels);
    }

    internal int Height { get; private set; }
    internal int FrameCount => _parts.Count;

    internal ScrollFrameResult TryAppend(BitmapSource frame)
    {
        if (frame.PixelWidth != _width || frame.PixelHeight != _frameHeight)
            return ScrollFrameResult.NoOverlap;

        var pixels = _frameBuffer ??= AllocateFrame();
        CopyPixels(frame, pixels);
        var rows = ReadRows(pixels);
        if (rows.Zip(_previousRows).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)))
            return ScrollFrameResult.Unchanged;

        var offset = FindOffset(_previousRows, rows);
        if (offset <= 0)
            return ScrollFrameResult.NoOverlap;

        var height = Height + offset;
        if (height > MaximumHeight || (long)height * _width > MaximumPixels)
            return ScrollFrameResult.Limit;

        _parts.Add(pixels.AsSpan((_frameHeight - offset) * _stride).ToArray());
        _previousRows = rows;
        Height = height;
        return ScrollFrameResult.Added;
    }

    internal BitmapSource Finish()
    {
        if (FrameCount == 1)
        {
            var first = BitmapSource.Create(_width, Height, 96, 96, PixelFormats.Bgr32, null, _parts[0], _stride);
            first.Freeze();
            return first;
        }

        var image = new WriteableBitmap(_width, Height, 96, 96, PixelFormats.Bgr32, null);
        var top = 0;
        foreach (var part in _parts)
        {
            var height = part.Length / _stride;
            image.WritePixels(new System.Windows.Int32Rect(0, 0, _width, height), part, _stride, 0, top);
            top += height;
        }
        image.Freeze();
        return image;
    }

    private byte[] AllocateFrame() => GC.AllocateUninitializedArray<byte>(checked(_stride * _frameHeight));

    private void CopyPixels(BitmapSource frame, byte[] pixels)
    {
        var image = frame.Format == PixelFormats.Bgr32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        image.CopyPixels(pixels, _stride, 0);
    }

    private ulong[][] ReadRows(byte[] pixels)
    {
        // Scrollbars and fixed sidebars can change independently of the page content.
        var inset = Math.Min(18, _width / 30);
        var contentWidth = _width - inset * 2;
        var stripCount = Math.Clamp(contentWidth / 42, 1, 5);
        var hashes = new ulong[stripCount][];
        for (var strip = 0; strip < hashes.Length; strip++)
        {
            var left = inset + strip * contentWidth / stripCount;
            var right = inset + (strip + 1) * contentWidth / stripCount;
            var step = Math.Max(1, (right - left) / 44) * 4;
            var rows = hashes[strip] = new ulong[_frameHeight];
            for (var y = 0; y < rows.Length; y++)
            {
                var hash = 14695981039346656037UL;
                var start = y * _stride;
                var end = start + right * 4;
                for (var x = start + left * 4; x < end; x += step)
                {
                    var color = pixels[x] | pixels[x + 1] << 8 | pixels[x + 2] << 16;
                    hash = (hash ^ (uint)(color & 0xF8F8F8)) * 1099511628211UL;
                }
                rows[y] = hash;
            }
        }
        return hashes;
    }

    private static int FindOffset(ulong[][] previous, ulong[][] current)
    {
        const int MinimumStripVotes = 6;
        var height = previous[0].Length;
        var maxOffset = height - Math.Max(32, height / 8);
        if (maxOffset <= 0)
            return 0;

        var votes = new int[previous.Length][];
        var totals = new int[maxOffset * 2 + 1];
        for (var strip = 0; strip < previous.Length; strip++)
        {
            var stripVotes = votes[strip] = CountOffsets(previous[strip], current[strip], maxOffset);
            var stationary = stripVotes[maxOffset];
            for (var index = 0; index < stripVotes.Length; index++)
                if (stripVotes[index] >= MinimumStripVotes && stripVotes[index] > stationary)
                    totals[index] += stripVotes[index];
        }

        var bestOffset = 0;
        var bestScore = 0.0;
        var otherScore = 0.0;
        var candidates = Enumerable.Range(0, totals.Length).Where(index => totals[index] >= 12)
            .OrderByDescending(index => totals[index]).Take(24);
        foreach (var index in candidates)
        {
            var offset = index - maxOffset;
            var score = 0.0;
            for (var strip = 0; strip < votes.Length; strip++)
            {
                var support = votes[strip][index];
                if (support < MinimumStripVotes || support <= votes[strip][maxOffset])
                    continue;

                var agreement = Agreement(previous[strip], current[strip], offset);
                if (agreement >= .72)
                    score += support * agreement;
            }
            if (score > bestScore)
            {
                otherScore = bestScore;
                bestScore = score;
                bestOffset = offset;
            }
            else
                otherScore = Math.Max(otherScore, score);
        }

        // Similar positive and negative matches can occur on repeating patterns.
        return bestScore >= 12 && otherScore < bestScore * .87 ? bestOffset : 0;
    }

    private static int[] CountOffsets(ulong[] previous, ulong[] current, int maxOffset)
    {
        const int RepeatedRowLimit = 5;
        var positions = new Dictionary<ulong, List<int>>();
        for (var row = 0; row < current.Length; row++)
        {
            if (!positions.TryGetValue(current[row], out var rows))
                positions.Add(current[row], [row]);
            else if (rows.Count <= RepeatedRowLimit)
                rows.Add(row);
        }

        var votes = new int[maxOffset * 2 + 1];
        for (var row = 0; row < previous.Length; row++)
        {
            if (!positions.TryGetValue(previous[row], out var rows) || rows.Count > RepeatedRowLimit)
                continue;

            // Common rows such as white space do not anchor an alignment.
            foreach (var next in rows)
            {
                var index = row - next + maxOffset;
                if ((uint)index < (uint)votes.Length)
                    votes[index]++;
            }
        }
        return votes;
    }

    private static double Agreement(ulong[] previous, ulong[] current, int offset)
    {
        var startPrevious = Math.Max(0, offset);
        var startCurrent = Math.Max(0, -offset);
        var overlap = previous.Length - Math.Abs(offset);
        var matching = 0;
        for (var row = 0; row < overlap; row++)
            if (previous[startPrevious + row] == current[startCurrent + row])
                matching++;
        return (double)matching / overlap;
    }
}
