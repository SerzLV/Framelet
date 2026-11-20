using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Framelet;

public partial class MainWindow
{
    private async void PasteImage_Click(object sender, RoutedEventArgs e) => await PasteImageAsync();

    private Task PasteImageAsync()
    {
        return RunOperation(async () =>
        {
            BitmapSource? image = null;
            await AccessClipboardAsync(() => image = ReadClipboardImage(Clipboard.GetDataObject()));
            if (image is null)
            {
                Report(UiText.Get("PasteNoImage"), true);
                return;
            }

            if (await SaveBeforeLeavingAsync())
                await OpenCapturedImageAsync(image, "ImagePasted");
        });
    }

    private static BitmapSource? ReadClipboardImage(IDataObject? clipboard)
    {
        if (clipboard is ITypedDataObject data)
        {
            try
            {
                if (data.TryGetData<MemoryStream>("PNG", autoConvert: false, out var buffer))
                {
                    var position = buffer.Position;
                    try
                    {
                        buffer.Position = 0;
                        return DecodeClipboardPng(buffer);
                    }
                    finally { buffer.Position = position; }
                }
                if (data.TryGetData<byte[]>("PNG", autoConvert: false, out var bytes))
                {
                    using var stream = new MemoryStream(bytes, writable: false);
                    return DecodeClipboardPng(stream);
                }
            }
            catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ObjectDisposedException)
            {
                Trace.WriteLine(error);
            }
        }

        var bitmap = clipboard?.GetData(DataFormats.Bitmap) as BitmapSource;
        if (bitmap?.IsFrozen == false)
            bitmap = bitmap.GetCurrentValueAsFrozen() as BitmapSource;
        return bitmap;
    }

    private static BitmapSource DecodeClipboardPng(Stream stream)
    {
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }
}
