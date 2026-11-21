using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Framelet;

public partial class MainWindow
{
    private const int ScrollingShortcutId = 8104;
    private const int LastCaptureShortcutId = 8105;
    private static readonly (int Id, uint Key, CaptureMode Mode)[] Shortcuts =
    [
        (8101, 0x31, CaptureMode.Region),
        (8102, 0x32, CaptureMode.Screen),
        (8103, 0x33, CaptureMode.Window),
        (ScrollingShortcutId, 0x34, CaptureMode.Scrolling)
    ];
    private static readonly int[] CaptureActionIds = [.. Shortcuts.Select(shortcut => shortcut.Id), LastCaptureShortcutId];

    private readonly ForegroundWindowTracker _foregroundTracker;
    private readonly PrintScreenShortcut _printScreen;
    private readonly Dictionary<int, int> _registered = [];
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private HwndSource? _windowSource;
    private ScrollCaptureWindow? _scrollCapture;
    private volatile bool _captureInProgress;
    private bool _quickCapturePending;

    private void RequestQuickCapture()
    {
        if (_quitting || !_printScreen.IsActive || _captureInProgress)
            return;
        if (_busy)
            _quickCapturePending = true;
        else
            _ = CaptureAsync(CaptureMode.QuickClipboard);
    }

    private void RunPendingQuickCapture()
    {
        if (_busy || !_quickCapturePending)
            return;
        _quickCapturePending = false;
        Dispatcher.InvokeAsync(RequestQuickCapture);
    }

    private void AttachCaptureCommands()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        // An animated hide can leave the workspace visible in the frame being captured.
        const uint transitionsForcedDisabled = 3;
        var disableTransitions = 1;
        Native.DwmSetWindowAttribute(handle, transitionsForcedDisabled, ref disableTransitions, sizeof(int));
        _windowSource?.AddHook(WindowMessage);
        ApplyHotkeys();
        CreateTray();
        _foregroundTracker.Start();
    }

    private void DetachCaptureCommands()
    {
        _foregroundTracker.Dispose();
        _printScreen.Dispose();
        UnregisterHotkeys();
        _windowSource?.RemoveHook(WindowMessage);
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
        _trayIcon?.Dispose();
    }

    private void CreateTray()
    {
        var iconResource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Framelet.ico"));
        if (iconResource is null)
            return;

        using (var stream = iconResource.Stream)
        using (var loadedIcon = new Drawing.Icon(stream))
            _trayIcon = (Drawing.Icon)loadedIcon.Clone();

        var menu = new TrayMenu(_trayIcon);
        menu.AddCommand("TrayOpen", "Launch", (_, _) => Dispatcher.Invoke(ShowWorkspace));
        menu.AddCommand("OpenLastCapture", "Launch", (_, _) => RequestOpenLastCapture());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.AddCommand("CaptureQuickClipboard", "Crop", async (_, _) => await CaptureAsync(CaptureMode.QuickClipboard));
        menu.AddCommand("CaptureRegion", "Pen", async (_, _) => await CaptureAsync(CaptureMode.Region));
        menu.AddCommand("CaptureScreen", "Screen", async (_, _) => await CaptureAsync(CaptureMode.Screen));
        menu.AddCommand("CaptureWindow", "Window", async (_, _) => await CaptureAsync(CaptureMode.Window));
        menu.AddCommand("CaptureDelayed", "Timer", async (_, _) => await CaptureAsync(CaptureMode.Delayed));
        menu.AddCommand("CaptureScrolling", "Scroll", async (_, _) => await CaptureAsync(CaptureMode.Scrolling));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.AddCommand("TrayExit", "Power", async (_, _) => await RunOperation(async () =>
        {
            if (!await SaveBeforeLeavingAsync())
            {
                ShowWorkspace();
                return;
            }
            _quitting = true;
            Close();
        }));

        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Framelet", ContextMenuStrip = menu, Visible = true };
        UpdateTrayShortcuts();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowWorkspace();
        };
    }

    private void UpdateTrayShortcuts()
    {
        if (_tray?.ContextMenuStrip is not TrayMenu menu)
            return;

        foreach (var shortcut in Shortcuts)
        {
            var text = _registered.ContainsKey(shortcut.Id) ? CaptureGesture(shortcut.Id).Display("+") : "";
            menu.SetShortcut("Capture" + shortcut.Mode, text);
        }
        menu.SetShortcut("CaptureQuickClipboard", _printScreen.IsActive ? "PrtSc" : "");
        menu.SetShortcut("OpenLastCapture", _registered.ContainsKey(LastCaptureShortcutId)
            ? CaptureGesture(LastCaptureShortcutId).Display("+") : "");
        menu.SetShortcut("TrayOpen", _workspaceHotkeyId is not null ? WorkspaceGesture.Display("+") : "");
    }

    private void ApplyHotkeys()
    {
        UnregisterHotkeys();
        if (_preferences.Hotkeys && _windowSource is not null)
        {
            foreach (var id in CaptureActionIds)
            {
                if (RegisterWorkspaceHotkey(id, CaptureGesture(id)))
                    _registered.Add(id, id);
            }
        }

        RegisterWorkspaceHotkey();
        _printScreen.Configure(_preferences.Hotkeys && _preferences.PrintScreen);
        UpdateHotkeyLabels();
    }

    private void UpdateHotkeyLabels()
    {
        var hasRegion = _registered.ContainsKey(Shortcuts[0].Id);
        var region = CaptureGesture(Shortcuts[0].Id).Display();
        var hasPrintScreen = _printScreen.IsActive;
        ClipboardKeycap.Visibility = hasPrintScreen ? Visibility.Visible : Visibility.Collapsed;
        EditorRegionShortcutLabel.Visibility = hasRegion ? Visibility.Visible : Visibility.Collapsed;
        EditorRegionShortcutLabel.Text = region;
        ScrollShortcutLabel.Text = CaptureGesture(ScrollingShortcutId).Display();
        ScrollShortcutLabel.Visibility = _registered.ContainsKey(ScrollingShortcutId) ? Visibility.Visible : Visibility.Collapsed;
        FooterHint.Text = hasPrintScreen ? UiText.Get("FooterPrintRegion") : hasRegion ? UiText.Format("FooterRegion", region) : "";
        HotkeysInfo.Text = !_preferences.Hotkeys
            ? UiText.Get("HotkeysOff") : "";
        if (_preferences.Hotkeys && _preferences.PrintScreen && !hasPrintScreen)
            HotkeysInfo.Text = UiText.Get("PrintUnavailable");
        HotkeysInfo.Visibility = HotkeysInfo.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        PrintScreenSetting.IsEnabled = _preferences.Hotkeys;
        RefreshCaptureShortcuts();
        RefreshWorkspaceShortcut();
        UpdateTrayShortcuts();
    }

    private static string CaptureLabelKey(CaptureMode mode) => mode switch
    {
        CaptureMode.Region => "ShortcutRegion",
        CaptureMode.Screen => "ShortcutScreen",
        CaptureMode.Scrolling => "ShortcutScrolling",
        _ => "ShortcutWindow"
    };

    private void UnregisterHotkeys()
    {
        UnregisterWorkspaceHotkey();
        if (_windowSource is not null)
        {
            foreach (var id in _registered.Values)
                Native.UnregisterHotKey(_windowSource.Handle, id);
        }
        _registered.Clear();
    }

    private IntPtr WindowMessage(IntPtr handle, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.HotkeyMessage)
        {
            var id = wParam.ToInt32();
            if (InterceptShortcutWhileRecording(id))
            {
                handled = true;
                return IntPtr.Zero;
            }
            if (id == _workspaceHotkeyId)
            {
                handled = true;
                ToggleWorkspace();
                return IntPtr.Zero;
            }
            var action = CaptureActionForRegistration(id);
            if (action == LastCaptureShortcutId)
            {
                handled = true;
                RequestOpenLastCapture();
                return IntPtr.Zero;
            }
            foreach (var shortcut in Shortcuts)
            {
                if (action != shortcut.Id)
                    continue;

                handled = true;
                if (shortcut.Mode == CaptureMode.Scrolling && _scrollCapture is { } capture)
                    _ = capture.CaptureNextFrameAsync();
                else
                    _ = CaptureAsync(shortcut.Mode);
                break;
            }
        }

        return IntPtr.Zero;
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<CaptureMode>(tag, out var mode))
            await CaptureAsync(mode);
    }

    private Task CaptureAsync(CaptureMode mode)
    {
        return RunOperation(async () =>
        {
            if (mode is CaptureMode.QuickClipboard or CaptureMode.Delayed)
            {
                await CaptureQuickToClipboardAsync(mode);
                return;
            }

            var previousWindow = _foregroundTracker.Recall();
            var returnOnCancel = IsVisible && WindowState != WindowState.Minimized;
            CloseSettings();
            CloseRename(returnFocus: false);
            if (!await SaveBeforeLeavingAsync())
                return;
            Hide();

            try
            {
                await Task.Delay(65);
                Native.DwmFlush();
                var selection = mode == CaptureMode.Region ? await SelectRegionAsync(mode) : null;
                BitmapSource? image = mode switch
                {
                    CaptureMode.Screen => await Task.Run(CaptureService.CaptureCurrentDisplay),
                    CaptureMode.Window => await Task.Run(() => CaptureService.CaptureWindow(previousWindow)),
                    CaptureMode.Scrolling => await CaptureScrollingAsync(),
                    _ => selection?.Snapshot.Image
                };

                if (image is null)
                {
                    ReportCaptureCanceled(returnOnCancel);
                    return;
                }

                RecordCapture(image);
                if (selection is { Output: SelectionOutput.Copy })
                {
                    await CopyImageToClipboardAsync(image);
                    if (returnOnCancel)
                        ShowWorkspace();
                    Report(UiText.Get("ImageCopied"));
                    await RememberClipboardCaptureAsync(image);
                    return;
                }
                await OpenCapturedImageAsync(image, "CaptureReady");
            }
            catch
            {
                ShowWorkspace();
                throw;
            }
        }, capturing: true);
    }

    private void ReportCaptureCanceled(bool returnOnCancel)
    {
        if (returnOnCancel)
            ShowWorkspace();
        Report(UiText.Get("CaptureCanceled"));
    }

    private async Task CaptureQuickToClipboardAsync(CaptureMode mode)
    {
        var wasVisible = IsVisible;
        var wasActive = IsActive;
        RegionCapture? capture;
        Hide();
        try
        {
            if (mode == CaptureMode.Delayed && !await AuxiliaryWindows.ShowCaptureCountdownAsync())
            {
                Report(UiText.Get("CaptureCanceled"));
                return;
            }
            await Task.Delay(65);
            Native.DwmFlush();
            capture = await SelectRegionAsync(mode);
            if (capture is null)
            {
                Report(UiText.Get("CaptureCanceled"));
                return;
            }

            var image = capture.Snapshot.Image;
            if (capture.Output == SelectionOutput.Editor)
            {
                CloseSettings();
                CloseRename(returnFocus: false);
                if (await SaveBeforeLeavingAsync())
                {
                    RecordCapture(image);
                    await OpenCapturedImageAsync(image, "CaptureReady", capture.Document);
                }
                return;
            }

            RecordCapture(image);
            if (capture is { Output: SelectionOutput.Save, FilePath: { } path })
                Report(UiText.Format("SavedFile", Path.GetFileName(path)));
            else if (capture.Output == SelectionOutput.Pin)
                PinImage(image);
            else
            {
                await CopyImageToClipboardAsync(image);
                Report(UiText.Get("ImageCopied"));
            }
        }
        finally
        {
            if (wasVisible)
            {
                if (wasActive)
                    ShowWorkspace();
                else
                {
                    var showActivated = ShowActivated;
                    ShowActivated = false;
                    try { Show(); }
                    finally { ShowActivated = showActivated; }
                }
            }
        }

        await RememberClipboardCaptureAsync(capture.Snapshot.Image, capture.Document);
    }

    private async Task RememberClipboardCaptureAsync(BitmapSource image, EditorDocument? document = null)
    {
        if (_preferences.KeepHistory)
        {
            var item = await Task.Run(() => _history.Put(_history.CreateEntryPath(), image, document));
            LinkCaptureToHistory(image, item.FilePath);
            if (_initialHistoryLoad is { } loading)
            {
                await loading;
                AddRecent(item);
            }
        }
    }

    private async Task<BitmapSource?> CaptureScrollingAsync()
    {
        if (await SelectRegionAsync(CaptureMode.Scrolling) is not { Snapshot: var selection })
            return null;

        var scrollTarget = new ScrollWheelTarget(selection.Bounds);
        var stitcher = await Task.Run(() => new ScrollStitcher(selection.Image));
        var shortcut = _registered.ContainsKey(ScrollingShortcutId) ? CaptureGesture(ScrollingShortcutId).Display() : null;
        var capture = new ScrollCaptureWindow(selection, stitcher, scrollTarget, shortcut);
        _scrollCapture = capture;
        try
        {
            return await capture.RunAsync();
        }
        finally
        {
            _scrollCapture = null;
        }
    }

    private async Task<RegionCapture?> SelectRegionAsync(CaptureMode mode)
    {
        var (promptKey, adjustPromptKey) = mode switch
        {
            CaptureMode.Scrolling => ("ScrollSelectionPrompt", "ScrollSelectionAdjustPrompt"),
            CaptureMode.Region => ("SelectionPrompt", "EditorSelectionAdjustPrompt"),
            _ => ("QuickCapturePrompt", "ClipboardSelectionAdjustPrompt")
        };
        var singleDisplay = mode == CaptureMode.Scrolling;
        var (screens, candidates) = await Task.Run(() => (CaptureService.CaptureDisplays(),
            singleDisplay ? [] : CaptureService.GetVisibleWindowBounds()));
        var session = new RegionSelection(screens.Select(screen => screen.Bounds).ToArray(), candidates, singleDisplay);
        session.MovePointer(CaptureService.CursorPosition);
        var completion = new TaskCompletionSource<SelectionRequest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ink = PaletteSwatch(_palette?.Color)?.Tag is string color
            ? (Color)ColorConverter.ConvertFromString(color) : EditorImage.Ink;
        using var edits = mode is CaptureMode.QuickClipboard or CaptureMode.Delayed
            ? new SelectionEditingSession(session, screens, ink, EditorImage.StrokeWidth, EditorImage.NewCurveHasBothHeads,
                SelectionColors, RememberColor, text => AccessClipboardAsync(() => Clipboard.SetText(text)),
                request => completion.TrySetResult(request), () => completion.TrySetResult(null))
            : null;
        Action<KeyEventArgs>? editKey = edits is null ? null : edits.HandleKey;
        var windows = screens.Select((screen, index) => new SelectionWindow(screen, index == 0, promptKey, adjustPromptKey, session,
            region => completion.TrySetResult(new SelectionRequest(region)),
            singleDisplay ? null : region => completion.TrySetResult(new SelectionRequest(region, SelectionOutput.Copy)),
            () => completion.TrySetResult(null), editKey)).ToArray();

        SelectionRequest? selection;
        try
        {
            foreach (var window in windows.Reverse())
            {
                window.Closed += (_, _) => completion.TrySetResult(null);
                window.Show();
            }
            selection = await completion.Task;
        }
        finally
        {
            edits?.Dispose();
            foreach (var window in windows)
            {
                if (window.IsVisible)
                    window.Close();
            }
        }
        if (selection is not { } captured)
            return null;

        var image = captured.Rendered ?? await Task.Run(() => CaptureService.CropDisplays(screens, captured.Bounds));
        return new RegionCapture(new ScreenSnapshot(captured.Bounds, image), captured.Output, captured.Document, captured.FilePath);
    }

    private IEnumerable<SelectionSwatch> SelectionColors()
    {
        foreach (var swatch in ColorButtons.Children.OfType<Button>())
            yield return new SelectionSwatch((string)swatch.Tag, swatch.ToolTip?.ToString() ?? (string)swatch.Tag);
        foreach (var tag in _palette?.RecentColors ?? [])
        {
            if (PaletteSwatch(tag) is { } swatch)
                yield return new SelectionSwatch(tag, swatch.ToolTip?.ToString() ?? tag, Recent: true);
        }
    }
}
