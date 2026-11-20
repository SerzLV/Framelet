using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    private bool _quickCapturePending;

    private void RequestQuickCapture()
    {
        if (_quitting || !_printScreen.IsActive)
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
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.AddCommand("CaptureQuickClipboard", "Crop", async (_, _) => await CaptureAsync(CaptureMode.QuickClipboard));
        menu.AddCommand("OpenLastCapture", "Launch", (_, _) => RequestOpenLastCapture());
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
            if (mode == CaptureMode.QuickClipboard)
            {
                await CaptureQuickToClipboardAsync();
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
                await Task.Delay(mode == CaptureMode.Delayed ? 3000 : 65);
                Native.DwmFlush();
                BitmapSource? image = mode switch
                {
                    CaptureMode.Screen => await Task.Run(CaptureService.CaptureCurrentDisplay),
                    CaptureMode.Window => await Task.Run(() => CaptureService.CaptureWindow(previousWindow)),
                    CaptureMode.Scrolling => await CaptureScrollingAsync(),
                    _ => (await SelectRegionAsync("SelectionPrompt"))?.Image
                };

                if (image is null)
                {
                    if (returnOnCancel)
                        ShowWorkspace();
                    Report(UiText.Get("CaptureCanceled"));
                    return;
                }

                RecordCapture(image);
                await OpenCapturedImageAsync(image, "CaptureReady");
            }
            catch
            {
                ShowWorkspace();
                throw;
            }
        });
    }

    private async Task CaptureQuickToClipboardAsync()
    {
        var wasVisible = IsVisible;
        var wasActive = IsActive;
        BitmapSource? image;
        Hide();
        try
        {
            await Task.Delay(65);
            Native.DwmFlush();
            image = (await SelectRegionAsync("QuickCapturePrompt"))?.Image;
            if (image is null)
            {
                Report(UiText.Get("CaptureCanceled"));
                return;
            }

            RecordCapture(image);
            await CopyImageToClipboardAsync(image);
            Report(UiText.Get("ImageCopied"));
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

        if (_preferences.KeepHistory)
        {
            var item = await Task.Run(() => _history.Put(_history.CreateEntryPath(), image));
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
        var selection = await SelectRegionAsync("ScrollSelectionPrompt");
        if (selection is null)
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

    private async Task<ScreenSnapshot?> SelectRegionAsync(string promptKey)
    {
        var screens = await Task.Run(CaptureService.CaptureDisplays);
        var completion = new TaskCompletionSource<ScreenSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = screens.Select((screen, index) => new SelectionWindow(screen, index == 0, promptKey,
            (snapshot, region) => completion.TrySetResult(new ScreenSnapshot(
                new Drawing.Rectangle(snapshot.Bounds.X + region.X, snapshot.Bounds.Y + region.Y, region.Width, region.Height),
                CaptureService.Crop(snapshot.Image, region))),
            () => completion.TrySetResult(null))).ToArray();

        try
        {
            foreach (var window in windows.Reverse())
            {
                window.Closed += (_, _) => completion.TrySetResult(null);
                window.Show();
            }
            return await completion.Task;
        }
        finally
        {
            foreach (var window in windows)
            {
                if (window.IsVisible)
                    window.Close();
            }
        }
    }
}
