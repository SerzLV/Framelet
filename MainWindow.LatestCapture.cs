using System.Windows;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Framelet;

public partial class MainWindow
{
    private BitmapSource? _lastCaptureImage;
    private string? _lastCapturePath;
    private Drawing.Point? _lastCaptureOrigin;
    private bool _publishingCapture;
    private bool _openLastCapturePending;

    private void RecordCapture(BitmapSource image, Drawing.Point? origin)
    {
        _lastCaptureImage = image;
        _lastCapturePath = null;
        _lastCaptureOrigin = origin;
        _publishingCapture = true;
    }

    private void LinkCaptureToHistory(BitmapSource? image, string path)
    {
        if (image is null || !ReferenceEquals(image, _lastCaptureImage))
            return;

        _lastCapturePath = path;
        _lastCaptureImage = null;
    }

    private void RequestOpenLastCapture()
    {
        if (_busy)
        {
            _openLastCapturePending |= _publishingCapture;
            return;
        }

        _ = OpenLastCaptureAsync();
    }

    private void CompleteCaptureRequest()
    {
        _publishingCapture = false;
        if (!_openLastCapturePending)
            return;

        _openLastCapturePending = false;
        RequestOpenLastCapture();
    }

    private Task OpenLastCaptureAsync() => RunOperation(async () =>
    {
        var image = _lastCaptureImage;
        var path = _lastCapturePath;
        var origin = _lastCaptureOrigin;
        if (image is null && (path is null || !File.Exists(path)))
        {
            await EnsureHistoryLoadedAsync();
            path = _recent.FirstOrDefault(entry => File.Exists(entry.FilePath))?.FilePath;
            origin = null;
        }

        CloseSettings();
        CloseRename(returnFocus: false);
        if (image is null && path is null)
        {
            ShowWorkspace();
            Report(UiText.Get("LastCaptureMissing"));
            return;
        }

        if (EditorView.Visibility == Visibility.Visible && (path is not null && _entryPath == path
            || image is not null && EditorImage.IsShowingSource(image)))
        {
            ShowWorkspace();
            return;
        }

        if (!await SaveBeforeLeavingAsync())
        {
            ShowWorkspace();
            return;
        }

        ShowWorkspace();
        if (path is not null)
        {
            var document = await Task.Run(() => _history.OpenEditorDocument(path));
            OpenEditor(document.Image, path, document.Journal, origin);
            Report(UiText.Get("LastCaptureOpened"));
        }
        else
            await OpenCapturedImageAsync(image!, "LastCaptureOpened", origin: origin);
    });
}
