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
    private const string ScrollingShortcutText = "Ctrl + Shift + 4";
    private static readonly (int Id, uint Key, CaptureMode Mode)[] Shortcuts =
    [
        (8101, 0x31, CaptureMode.Region),
        (8102, 0x32, CaptureMode.Screen),
        (8103, 0x33, CaptureMode.Window),
        (ScrollingShortcutId, 0x34, CaptureMode.Scrolling)
    ];

    private readonly ForegroundWindowTracker _foregroundTracker;
    private readonly PrintScreenShortcut _printScreen;
    private readonly HashSet<int> _registered = [];
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private HwndSource? _windowSource;
    private ScrollCaptureWindow? _scrollCapture;

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

        for (var index = 0; index < Shortcuts.Length; index++)
        {
            var shortcut = Shortcuts[index];
            var text = _registered.Contains(shortcut.Id) ? $"Ctrl+Shift+{index + 1}" : "";
            menu.SetShortcut("Capture" + shortcut.Mode, text);
        }
        menu.SetShortcut("CaptureQuickClipboard", _printScreen.IsActive ? "PrtSc" : "");
        menu.SetShortcut("OpenLastCapture", _registered.Contains(LastCaptureShortcutId) ? "Ctrl+Shift+5" : "");
        menu.SetShortcut("TrayOpen", _workspaceHotkeyId is not null ? WorkspaceGesture.Display("+") : "");
    }

    private void ApplyHotkeys()
    {
        UnregisterHotkeys();
        if (_preferences.Hotkeys && _windowSource is not null)
        {
            foreach (var (id, key, _) in Shortcuts)
            {
                if (Native.RegisterHotKey(_windowSource.Handle, id, Native.ControlShift, key))
                    _registered.Add(id);
            }
            if (Native.RegisterHotKey(_windowSource.Handle, LastCaptureShortcutId, Native.ControlShift, 0x35))
                _registered.Add(LastCaptureShortcutId);
        }

        RegisterWorkspaceHotkey();
        _printScreen.Configure(_preferences.Hotkeys && _preferences.PrintScreen);
        UpdateHotkeyLabels();
    }

    private void UpdateHotkeyLabels()
    {
        var hasRegion = _registered.Contains(Shortcuts[0].Id);
        var hasPrintScreen = _printScreen.IsActive;
        ClipboardKeycap.Visibility = hasPrintScreen ? Visibility.Visible : Visibility.Collapsed;
        EditorRegionShortcutLabel.Visibility = hasRegion ? Visibility.Visible : Visibility.Collapsed;
        ScrollShortcutLabel.Text = ScrollingShortcutText;
        ScrollShortcutLabel.Visibility = _registered.Contains(ScrollingShortcutId) ? Visibility.Visible : Visibility.Collapsed;
        FooterHint.Text = hasPrintScreen ? UiText.Get("FooterPrintRegion") : hasRegion ? UiText.Get("FooterRegion") : "";
        HotkeysInfo.Text = !_preferences.Hotkeys
            ? UiText.Get("HotkeysOff")
            : string.Join(Environment.NewLine, Shortcuts.Select((shortcut, index) =>
                $"Ctrl + Shift + {index + 1} — {ShortcutLabel(shortcut.Mode)}{(_registered.Contains(shortcut.Id) ? "" : " " + UiText.Get("ShortcutOccupied"))}"));
        if (_preferences.Hotkeys && _preferences.PrintScreen && !hasPrintScreen)
            HotkeysInfo.Text += Environment.NewLine + UiText.Get("PrintUnavailable");
        if (_preferences.Hotkeys)
            HotkeysInfo.Text += Environment.NewLine + $"Ctrl + Shift + 5 — {UiText.Get("OpenLastCapture")}" +
                (_registered.Contains(LastCaptureShortcutId) ? "" : " " + UiText.Get("ShortcutOccupied"));

        PrintScreenSetting.IsEnabled = _preferences.Hotkeys;
        RefreshWorkspaceShortcut();
        UpdateTrayShortcuts();
    }

    private static string ShortcutLabel(CaptureMode mode) => mode switch
    {
        CaptureMode.Region => UiText.Get("ShortcutRegion"),
        CaptureMode.Screen => UiText.Get("ShortcutScreen"),
        CaptureMode.Scrolling => UiText.Get("ShortcutScrolling"),
        _ => UiText.Get("ShortcutWindow")
    };

    private void UnregisterHotkeys()
    {
        UnregisterWorkspaceHotkey();
        if (_windowSource is not null)
        {
            foreach (var id in _registered)
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
            if (id == LastCaptureShortcutId)
            {
                handled = true;
                RequestOpenLastCapture();
                return IntPtr.Zero;
            }
            foreach (var shortcut in Shortcuts)
            {
                if (id != shortcut.Id)
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
        var shortcut = _registered.Contains(ScrollingShortcutId) ? ScrollingShortcutText : null;
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
