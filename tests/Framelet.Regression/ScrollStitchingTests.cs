using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Framelet;

internal static partial class Program
{
    private const int Height = 600;
    private const int Width = 1060;

    private static void ScrollStitchingPreservesContent()
    {
        var document = DrawCards();
        BitmapSource Picture(int y, int height) => new CroppedBitmap(document, new Int32Rect(0, y, Width, height));
        BitmapSource Frame(int y) => Picture(y, Height);

        var start = Frame(0);
        var stitcher = new ScrollStitcher(start);
        Require(stitcher.TryAppend(Frame(168)) == ScrollFrameResult.Added, "Ordinary webpage scrolling");
        Require(stitcher.TryAppend(Frame(344)) == ScrollFrameResult.Added, "Continue scrolling");
        var finished = stitcher.Finish();
        Require(finished.PixelHeight == 944 && ToPixels(finished).SequenceEqual(ToPixels(Picture(0, 944))), "No missing or duplicate pixels on an ordinary page");

        Require(stitcher.TrySetTail(Picture(344, 735)) == ScrollFrameResult.Added, "Extend the last frame by 135px");
        Require(stitcher.Height == 1079 && ToPixels(stitcher.Finish()).SequenceEqual(ToPixels(Picture(0, 1079))),
            "Only the new rows are added, with no seam or duplication");
        Require(stitcher.TrySetTail(Picture(345, 735)) == ScrollFrameResult.NoOverlap && stitcher.Height == 1079,
            "A page moved after scrolling does not replace the attached bottom");
        Require(stitcher.TrySetTail(Picture(344, 665)) == ScrollFrameResult.Added && stitcher.Height == 1009
            && ToPixels(stitcher.Finish()).SequenceEqual(ToPixels(Picture(0, 1009))), "Adjust bottom replaces the previous bottom");

        var singleFrame = new ScrollStitcher(start);
        Require(singleFrame.TrySetTail(Picture(0, 700)) == ScrollFrameResult.Added
            && ToPixels(singleFrame.Finish()).SequenceEqual(ToPixels(Picture(0, 700))), "Extend a selection without scrolling");

        var noisy = new ScrollStitcher(WindowFrame(Frame(0), 0, 1));
        Require(noisy.TryAppend(WindowFrame(Frame(168), 168, 2)) == ScrollFrameResult.Added && noisy.Height == Height + 168,
            "Sidebar, moving scrollbar, blinking elements and fixed heading");
        Require(noisy.TryAppend(WindowFrame(Frame(338), 338, 3)) == ScrollFrameResult.Added && noisy.Height == Height + 338,
            "Continued scrolling while widgets redraw");
        Require(noisy.TryAppend(WindowFrame(Frame(338), 338, 4)) != ScrollFrameResult.Added, "Redrawing widgets alone cannot add a frame");
        Require(noisy.TryAppend(WindowFrame(Frame(168), 168, 5)) == ScrollFrameResult.NoOverlap, "Upward scrolling does not append");
        Require(noisy.TryAppend(WindowFrame(Frame(1000), 1000, 6)) == ScrollFrameResult.NoOverlap, "Nonoverlapping content is rejected");

        var repeated = new ScrollStitcher(RepeatedBands(0));
        Require(repeated.TryAppend(RepeatedBands(117)) == ScrollFrameResult.NoOverlap, "Repetitive patterns are not stitched arbitrarily");
        var still = new ScrollStitcher(start);
        Require(still.TryAppend(start) == ScrollFrameResult.Unchanged, "Stationary image recognized");
        var largerStep = new ScrollStitcher(start);
        Require(largerStep.TryAppend(Frame(512)) == ScrollFrameResult.Added && largerStep.Height == Height + 512,
            "A 512px wheel movement in a 600px viewport preserves the remaining overlap");

        var longPage = new ScrollStitcher(LongFrame(0));
        for (var y = 384; y + 480 <= 32_000; y += 384)
            if (longPage.TryAppend(LongFrame(y)) != ScrollFrameResult.Added || longPage.Height != y + 480)
                throw new InvalidOperationException($"Long page alignment failed at {y}");
        var longImage = longPage.Finish();
        var lastRow = new byte[640 * 4];
        var expectedRow = new byte[640 * 4];
        longImage.CopyPixels(new Int32Rect(0, longImage.PixelHeight - 1, 640, 1), lastRow, lastRow.Length, 0);
        LongFrame(longImage.PixelHeight - 480).CopyPixels(new Int32Rect(0, 479, 640, 1), expectedRow, expectedRow.Length, 0);
        Require(longImage.PixelHeight > 31_000 && lastRow.SequenceEqual(expectedRow), "Long capture preserves the last row past the old limit");
        Require(longPage.TryAppend(LongFrame(longImage.PixelHeight - 480 + 384)) == ScrollFrameResult.Limit,
            "Capture still stops at the bounded height limit");

    }

    private static void ScrollStitchingKeepsOneFooter()
    {
        const int frameHeight = 400;
        const int footerHeight = 60;
        const int contentHeight = frameHeight - footerHeight;

        var page = new ScrollStitcher(FrameWithBand(0, frameHeight, contentHeight, footerHeight));
        foreach (var y in new[] { 150, 300, 450 })
            Require(page.TryAppend(FrameWithBand(y, frameHeight, contentHeight, footerHeight)) == ScrollFrameResult.Added,
                $"Frame with a fixed footer appends at offset {y}");

        var result = page.Finish();
        Require(result.PixelHeight == 850 && page.FrameCount == 4 &&
            ToPixels(result).SequenceEqual(ToPixels(FrameWithBand(0, 850, 790, footerHeight))),
            "A fixed 60px footer appears once, after all 790 scrolling rows");
        Require(page.TrySetTail(FrameWithBand(450, frameHeight + 25, contentHeight, footerHeight)) == ScrollFrameResult.Added
            && ToPixels(page.Finish()).SequenceEqual(ToPixels(FrameWithBand(0, 875, 790, footerHeight))),
            "Extending the final frame keeps the footer and the extra rows in order");

        var fast = new ScrollStitcher(FrameWithBand(0, frameHeight, contentHeight, footerHeight));
        Require(fast.TryAppend(FrameWithBand(245, frameHeight, contentHeight, footerHeight)) == ScrollFrameResult.Added &&
            ToPixels(fast.Finish()).SequenceEqual(ToPixels(FrameWithBand(0, 645, 585, footerHeight))),
            "A fixed footer does not consume the overlap on a large scroll step");

        var slow = new ScrollStitcher(FrameWithBand(0, frameHeight, contentHeight, footerHeight));
        Require(slow.TryAppend(FrameWithBand(1, frameHeight, contentHeight, footerHeight)) == ScrollFrameResult.Added &&
            slow.TryAppend(FrameWithBand(151, frameHeight, contentHeight, footerHeight)) == ScrollFrameResult.Added &&
            ToPixels(slow.Finish()).SequenceEqual(ToPixels(FrameWithBand(0, 551, 491, footerHeight))),
            "A one-pixel initial scroll does not leave a footer seam");

        const int blankStart = 320;
        var blank = new ScrollStitcher(FrameWithBand(0, frameHeight, blankStart, frameHeight - blankStart));
        Require(blank.TryAppend(FrameWithBand(100, frameHeight, blankStart - 100, frameHeight + 100 - blankStart)) == ScrollFrameResult.Added &&
            ToPixels(blank.Finish()).SequenceEqual(ToPixels(FrameWithBand(0, 500, blankStart, 500 - blankStart))),
            "Scrolling blank space is preserved when it matches at the scroll offset");
    }

    private static BitmapSource FrameWithBand(int offset, int height, int bandTop, int bandHeight)
    {
        var frame = LongFrame(offset, height);
        var stride = frame.PixelWidth * 4;
        var pixels = ToPixels(frame);
        pixels.AsSpan(bandTop * stride, bandHeight * stride).Fill(0xD5);
        return BitmapSource.Create(frame.PixelWidth, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
    }

    private static BitmapSource LongFrame(int y, int height = 480)
    {
        const int width = 640;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var rowKey = (uint)(y + row + 1) * 2654435761u;
            for (var x = 0; x < width; x++)
            {
                var key = rowKey ^ ((uint)x * 1234567u);
                var index = (row * width + x) * 4;
                pixels[index] = (byte)(key >> 5);
                pixels[index + 1] = (byte)(key >> 14);
                pixels[index + 2] = (byte)(key >> 22);
                pixels[index + 3] = byte.MaxValue;
            }
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
    }

    private static BitmapSource DrawCards()
    {
        var scene = new DrawingVisual();
        using (var draw = scene.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, 1850));
            var colors = new[] { Brushes.SlateBlue, Brushes.SeaGreen, Brushes.SteelBlue, Brushes.Peru, Brushes.DarkCyan };
            var rand = new Random(173);
            for (var index = 0; index < 33; index++)
            {
                var x = 19 + index % 3 * 278;
                var y = 23 + index / 3 * 163;
                draw.DrawRoundedRectangle(colors[index % colors.Length], null, new Rect(x, y, 249, 91), 7, 7);
                for (var line = 0; line < 16; line++)
                {
                    var stroke = new Pen(Brushes.WhiteSmoke, 1 + rand.Next(3));
                    var px = x + 13 + rand.Next(174);
                    var py = y + 4 + rand.Next(76);
                    draw.DrawLine(stroke, new Point(px, py), new Point(px + 7 + rand.Next(45), py + rand.Next(12)));
                }
                var title = new FormattedText($"Card {index:D2} · News and analysis", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 16, Brushes.Black, 1);
                draw.DrawText(title, new Point(x, y + 99));
                draw.DrawRectangle(Brushes.Gray, null, new Rect(x, y + 130, 40 + rand.Next(170), 2));
            }
        }
        var rendered = new RenderTargetBitmap(Width, 1850, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(scene);
        var bgr = new FormatConvertedBitmap(rendered, PixelFormats.Bgr32, null, 0);
        bgr.Freeze();
        return bgr;
    }

    private static BitmapSource WindowFrame(BitmapSource frame, int y, int tick)
    {
        var pixels = ToPixels(frame);
        var random = new Random(305 + tick);
        for (var row = 0; row < Height; row++)
        {
            for (var x = 865; x < Width; x++)
            {
                var offset = 4 * (row * Width + x);
                var screenFixedValue = (row / 7 + x / 9 + (x > 1018 ? (row >= y / 3 && row < y / 3 + 70 ? 144 : 205) : 40)) % 255;
                pixels[offset] = (byte)screenFixedValue;
                pixels[offset + 1] = (byte)(screenFixedValue * 3 + tick);
                pixels[offset + 2] = (byte)(screenFixedValue + 37);
            }
            for (var x = 0; x < Width; x++)
            {
                if (row >= 83 && (x >= 262 || row < 248 || row > 388))
                    continue;
                var offset = 4 * (row * Width + x);
                var value = row < 83 ? (byte)(30 + row / 4 + x % 63) : (byte)random.Next(110, 245);
                pixels[offset] = value;
                pixels[offset + 1] = (byte)(value * 2);
                pixels[offset + 2] = (byte)(value + 27);
            }
        }
        return BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, pixels, Width * 4);
    }

    private static BitmapSource RepeatedBands(int shift)
    {
        var pixels = new byte[Width * Height * 4];
        for (var row = 0; row < Height; row++)
            for (var x = 0; x < Width; x++)
            {
                var value = (byte)((row + shift) % 30 * 8 + x % 8);
                var index = (row * Width + x) * 4;
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
            }
        return BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, pixels, Width * 4);
    }

    private static byte[] ToPixels(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }
}
