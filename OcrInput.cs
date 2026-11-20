using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tesseract;

namespace Framelet;

internal static class OcrInput
{
    internal static Pix Create(BitmapSource image)
    {
        if (TryCopyOpaquePixels(image) is { } direct)
            return direct;

        // Preserve Tesseract's PNG handling for transparency and indexed formats.
        using var png = new MemoryStream();
        HistoryStore.EncodePng(png, image);
        return Pix.LoadFromMemory(png.ToArray());
    }

    private static unsafe Pix? TryCopyOpaquePixels(BitmapSource image)
    {
        var format = image.Format;
        var hasAlpha = format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32;
        if (format == PixelFormats.Bgr24)
            image = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        else if (format != PixelFormats.Bgr32 && !hasAlpha)
            return null;

        var pix = Pix.Create(image.PixelWidth, image.PixelHeight, 32);
        try
        {
            var data = pix.GetData();
            var length = checked(data.WordsPerLine * image.PixelHeight);
            image.CopyPixels(Int32Rect.Empty, data.Data, checked(length * sizeof(uint)), data.WordsPerLine * sizeof(uint));
            var pixels = new Span<uint>((void*)data.Data, length);
            foreach (ref var pixel in pixels)
            {
                if (hasAlpha && (pixel >> 24) != 255)
                {
                    pix.Dispose();
                    return null;
                }
                // WPF stores BGR in little-endian bytes. Leptonica expects 0xRRGGBB00.
                pixel <<= 8;
            }
            pix.XRes = (int)Math.Round(image.DpiX);
            pix.YRes = (int)Math.Round(image.DpiY);
            return pix;
        }
        catch
        {
            pix.Dispose();
            throw;
        }
    }
}
