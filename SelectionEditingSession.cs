using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Drawing = System.Drawing;

namespace Framelet;

internal sealed record SelectionRequest(Drawing.Rectangle Bounds, SelectionOutput Output = SelectionOutput.Default,
    BitmapSource? Rendered = null, EditorDocument? Document = null, string? FilePath = null);

internal sealed record RegionCapture(ScreenSnapshot Snapshot, SelectionOutput Output, EditorDocument? Document, string? FilePath);

internal sealed class SelectionEditingSession : IDisposable
{
    private readonly RegionSelection _selection;
    private readonly ScreenSnapshot[] _screens;
    private readonly SelectionToolbar _toolbar = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<string, Task> _copyText;
    private readonly Func<IEnumerable<SelectionSwatch>> _colors;
    private readonly Action<string> _rememberColor;
    private readonly Action<SelectionRequest> _complete;
    private readonly Action _cancel;
    private SelectionAnnotationWindow? _canvas;
    private Drawing.Rectangle _canvasBounds;
    private Drawing.Rectangle _toolbarBounds;
    private Color _ink;
    private double _stroke;
    private bool _heads;
    private bool _editing;
    private bool _hadMarks;
    private bool _busy;
    private bool _modal;
    private bool _disposed;
    private bool _toolbarClosed;
    private EditorSurface? Editor => _canvas?.Editor;

    internal SelectionEditingSession(RegionSelection selection, ScreenSnapshot[] screens,
        Color ink, double stroke, bool heads, Func<IEnumerable<SelectionSwatch>> colors, Action<string> rememberColor,
        Func<string, Task> copyText, Action<SelectionRequest> complete, Action cancel)
    {
        _selection = selection;
        _screens = screens;
        _ink = ink;
        _stroke = stroke;
        _heads = heads;
        _colors = colors;
        _rememberColor = rememberColor;
        _copyText = copyText;
        _complete = complete;
        _cancel = cancel;
        _selection.Changed += SyncSelection;
        _toolbar.PreviewKeyDown += (_, e) => HandleKey(e);
        _toolbar.Closed += (_, _) =>
        {
            _toolbarClosed = true;
            if (!_disposed)
                _cancel();
        };
        _toolbar.ToolRequested += SelectTool;
        _toolbar.ColorRequested += PickColor;
        _toolbar.StrokeChanged += width =>
        {
            if (_busy)
                return;
            _stroke = width;
            Editor?.SetStrokeWidth(width);
        };
        _toolbar.StrokeStarted += () => Editor?.StartStrokeAdjustment();
        _toolbar.StrokeCompleted += () => Editor?.CompleteStrokeAdjustment();
        _toolbar.HeadsChanged += both =>
        {
            if (_busy)
                return;
            _heads = both;
            Editor?.SetCurveHeads(both);
        };
        _toolbar.UndoRequested += Undo;
        _toolbar.RedoRequested += Redo;
        _toolbar.DeleteRequested += Delete;
        _toolbar.FrameRequested += AdjustFrame;
        _toolbar.OcrRequested += Recognize;
        _toolbar.OutputRequested += Output;
        _toolbar.CancelRequested += cancel;
        _toolbar.SetColors(_colors());
        RefreshTools();
    }

    internal void HandleKey(KeyEventArgs e)
    {
        if (e.Handled || _modal)
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _cancel();
            return;
        }
        if (_selection.IsDragging || _selection.Selection is null)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C: Output(SelectionOutput.Copy); break;
                case Key.S: Output(SelectionOutput.Save); break;
                case Key.E: Recognize(false); break;
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                default: return;
            }
        }
        else if (modifiers == ModifierKeys.None && e.Key == Key.Enter)
            Output(SelectionOutput.Copy);
        else if (modifiers == ModifierKeys.None && e.Key == Key.Delete)
            Delete();
        else if (modifiers == ModifierKeys.None && EditorSurface.ToolForKey(e.Key) is { } tool && tool != EditorTool.Hand)
            SelectTool(tool);
        else if (!_selection.IsLocked && e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            && (modifiers is ModifierKeys.None or ModifierKeys.Shift) && e.OriginalSource is not Slider)
        {
            var step = modifiers == ModifierKeys.Shift ? 10 : 1;
            _selection.Nudge(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0,
                e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
        }
        else
            return;
        e.Handled = true;
    }

    private void SyncSelection()
    {
        if (_disposed || _toolbarClosed)
            return;
        if (_selection.Selection is not { } region || _selection.IsDragging || _modal)
        {
            if (_toolbar.IsVisible)
                _toolbar.Hide();
            return;
        }
        if (_canvas is not null && region != _canvasBounds)
            CloseCanvas();
        if (!_toolbar.IsVisible || _toolbarBounds != region)
        {
            _toolbarBounds = region;
            _toolbar.ShowAt(region);
        }
    }

    private async Task<BitmapSource> CropAsync(Drawing.Rectangle region) =>
        await Task.Run(() => CaptureService.CropDisplays(_screens, region));

    private async Task<SelectionAnnotationWindow?> CanvasAsync(Drawing.Rectangle region, BitmapSource? image = null)
    {
        if (_canvas is not null && _canvasBounds == region)
            return _canvas;
        image ??= await CropAsync(region);
        if (_disposed)
            return null;

        CloseCanvas();
        _canvasBounds = region;
        var canvas = new SelectionAnnotationWindow(region, image);
        _canvas = canvas;
        var editor = canvas.Editor;
        editor.SetInk(_ink);
        editor.SetStrokeWidth(_stroke);
        editor.SetCurveHeads(_heads);
        editor.Changed += EditorChanged;
        editor.SelectionChanged += RefreshTools;
        editor.RequestText = AskForText;
        canvas.PreviewKeyDown += (_, e) => HandleKey(e);
        canvas.Closed += (_, _) =>
        {
            if (!_disposed && ReferenceEquals(_canvas, canvas))
            {
                _canvas = null;
                _cancel();
            }
        };
        return canvas;
    }

    private string? AskForText(string? initial)
    {
        if (_canvas is not { } canvas)
            return null;
        _modal = true;
        SyncSelection();
        try { return AuxiliaryWindows.AskForText(canvas, initial); }
        finally
        {
            _modal = false;
            SyncSelection();
        }
    }

    private void SelectTool(EditorTool tool) => Run(async region =>
    {
        if (await CanvasAsync(region) is not { } canvas)
            return;
        canvas.Editor.Tool = tool;
        ShowCanvas(canvas);
    });

    private void ShowCanvas(SelectionAnnotationWindow canvas)
    {
        _editing = true;
        _selection.SetLocked(true);
        canvas.ShowCanvas();
        _toolbar.Reposition();
        RefreshTools();
    }

    private void EditorChanged()
    {
        var hasMarks = Editor?.HasAnnotations == true;
        if (_hadMarks && !hasMarks)
            AdjustFrame();
        else if (hasMarks && !_editing && !_busy && _canvas is { } canvas)
            ShowCanvas(canvas);
        _hadMarks = hasMarks;
        RefreshTools();
    }

    private void RefreshTools() => _toolbar.UpdateEditor(Editor, _editing, _ink, _stroke, _heads);

    private void AdjustFrame()
    {
        if (_canvas is not { Editor.HasAnnotations: false } canvas)
            return;
        _editing = false;
        canvas.Editor.CancelDraft();
        canvas.Hide();
        _toolbar.Activate();
        _selection.SetLocked(_busy);
        RefreshTools();
    }

    private void PickColor(string tag)
    {
        if (_busy)
            return;
        _ink = (Color)ColorConverter.ConvertFromString(tag);
        Editor?.SetInk(_ink);
        _rememberColor(tag);
        _toolbar.SetColors(_colors());
        RefreshTools();
    }

    private void Undo() { if (!_busy) Editor?.Undo(); }
    private void Delete() { if (!_busy && _editing) Editor?.DeleteSelection(); }

    private void Redo() { if (!_busy) Editor?.Redo(); }

    private void Recognize(bool redact) => Run(async region =>
    {
        _toolbar.SetStatus(UiText.Get("OcrWorking"));
        var editor = Editor;
        var image = editor is null ? await CropAsync(region) : await editor.ExportForOcrAsync();
        if (_disposed)
            return;
        var result = await OcrService.RecognizeAsync(image, OcrService.AutomaticMode, _cancellation.Token);
        if (_disposed)
            return;
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            _toolbar.SetStatus(UiText.Get("OcrNoText"));
            return;
        }
        if (!redact)
        {
            await _copyText(result.Text);
            if (!_disposed)
                _toolbar.SetStatus(UiText.Get("OcrTextCopied"));
            return;
        }
        if (result.ContactRegions.Count == 0)
        {
            _toolbar.SetStatus(UiText.Get("SelectionNoContacts"));
            return;
        }
        if (await CanvasAsync(region, image) is not { } canvas)
            return;
        canvas.Editor.RedactContacts(result.ContactRegions);
        canvas.Editor.Tool = EditorTool.Select;
        ShowCanvas(canvas);
        _toolbar.SetStatus($"{UiText.Get("OcrContactsHidden")} {UiText.Get("OcrRedactSourceNote")}");
    });

    private void Output(SelectionOutput output) => Run(async region =>
    {
        string? path = null;
        if (output == SelectionOutput.Save)
        {
            var dialog = new SaveFileDialog
            {
                Filter = UiText.Get("PngFilter"), DefaultExt = ".png", AddExtension = true,
                FileName = HistoryStore.DefaultFileName(DateTime.Now)
            };
            _modal = true;
            try
            {
                if (dialog.ShowDialog(_canvas?.IsVisible == true ? _canvas : _toolbar) != true)
                    return;
                path = dialog.FileName;
            }
            finally { _modal = false; }
        }
        var editor = Editor;
        editor?.CancelDraft();
        editor?.CompleteStrokeAdjustment();
        var hasMarks = editor?.HasAnnotations == true;
        var document = hasMarks || output == SelectionOutput.Editor ? editor?.CaptureJournal() : null;
        var rendered = hasMarks ? await editor!.ExportAsync() : null;
        if (path is not null && !_disposed)
        {
            rendered ??= await CropAsync(region);
            if (_disposed)
                return;
            await Task.Run(() => HistoryStore.WritePng(path, rendered));
        }
        if (!_disposed)
            _complete(new SelectionRequest(region, output, rendered, document, path));
    });

    private async void Run(Func<Drawing.Rectangle, Task> action)
    {
        if (_disposed || _busy || _selection.IsDragging || _selection.Selection is not { } region)
            return;
        _busy = true;
        _selection.SetLocked(true);
        if (Editor is { } editor)
            editor.IsEnabled = false;
        try { await action(region); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            if (!_disposed)
                _toolbar.SetStatus(UiText.Get(error.GetBaseException() is DllNotFoundException ? "OcrNativeFailure" : "OperationFailure"));
        }
        finally
        {
            _busy = false;
            if (!_disposed)
            {
                if (Editor is { } current)
                    current.IsEnabled = true;
                _selection.SetLocked(_editing);
                RefreshTools();
            }
        }
    }

    private void CloseCanvas()
    {
        if (_canvas is not { } canvas)
            return;
        _canvas = null;
        _hadMarks = false;
        canvas.Editor.Changed -= EditorChanged;
        canvas.Editor.SelectionChanged -= RefreshTools;
        canvas.Close();
        RefreshTools();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _selection.Changed -= SyncSelection;
        _cancellation.Cancel();
        _cancellation.Dispose();
        CloseCanvas();
        if (!_toolbarClosed)
            _toolbar.Close();
    }
}
