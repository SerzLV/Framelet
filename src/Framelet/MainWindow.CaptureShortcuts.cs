using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Framelet;

public partial class MainWindow
{
    private readonly Dictionary<int, Button> _captureShortcutButtons = [];
    private int? _recordingCaptureShortcut;

    private TrayShortcut CaptureGesture(int id)
    {
        if (_preferences.CaptureShortcuts?.GetValueOrDefault(id) is { IsUsable: true } saved)
            return saved;
        var key = id == LastCaptureShortcutId ? 0x35 : (int)Shortcuts.First(shortcut => shortcut.Id == id).Key;
        return new TrayShortcut(KeyInterop.KeyFromVirtualKey(key), ModifierKeys.Control | ModifierKeys.Shift);
    }

    private int? CaptureActionForRegistration(int id)
    {
        foreach (var (action, registration) in _registered)
            if (registration == id)
                return action;
        return null;
    }

    private void InitializeCaptureShortcuts()
    {
        foreach (var id in CaptureActionIds)
        {
            var key = id == LastCaptureShortcutId ? "OpenLastCapture" : CaptureLabelKey(Shortcuts.First(item => item.Id == id).Mode);
            var button = new Button
            {
                MinWidth = 115,
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(10, 0, 0, 0),
                Tag = id
            };
            button.SetResourceReference(AutomationProperties.NameProperty, UiText.Key(key));
            button.SetResourceReference(ToolTipProperty, UiText.Key("WorkspaceShortcutHint"));
            button.Click += CaptureShortcut_Click;
            DockPanel.SetDock(button, Dock.Right);
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            label.SetResourceReference(TextBlock.TextProperty, UiText.Key(key));
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(button);
            row.Children.Add(label);
            CaptureShortcutOptions.Children.Add(row);
            _captureShortcutButtons.Add(id, button);
        }
    }

    private void CaptureShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: int id } button)
            return;
        if (_recordingCaptureShortcut == id)
        {
            StopRecordingCaptureShortcut();
            return;
        }
        if (_recordingWorkspaceShortcut)
            StopRecordingWorkspaceShortcut();
        _recordingCaptureShortcut = id;
        button.Focus();
        RefreshCaptureShortcuts();
        ShowCaptureShortcutStatus("WorkspaceShortcutRecorderHint");
    }

    private void RecordCaptureShortcut(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.IsRepeat || _recordingCaptureShortcut is not { } id)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecordingCaptureShortcut();
            return;
        }
        if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin))
            TryChangeCaptureShortcut(id, new TrayShortcut(key, Keyboard.Modifiers));
    }

    private void TryChangeCaptureShortcut(int id, TrayShortcut shortcut)
    {
        if (!shortcut.IsUsable)
        {
            ShowCaptureShortcutStatus("WorkspaceShortcutInvalid");
            return;
        }
        if (shortcut == WorkspaceGesture || CaptureActionIds.Any(other => other != id && CaptureGesture(other) == shortcut))
        {
            ShowCaptureShortcutStatus("WorkspaceShortcutInUse");
            return;
        }
        if (shortcut == CaptureGesture(id) && (!_preferences.Hotkeys || _registered.ContainsKey(id)))
        {
            StopRecordingCaptureShortcut();
            return;
        }

        var existing = _registered.GetValueOrDefault(id);
        var replacement = existing == id ? id + 20 : id;
        var register = _preferences.Hotkeys;
        if (register && !RegisterWorkspaceHotkey(replacement, shortcut))
        {
            ShowCaptureShortcutStatus("WorkspaceShortcutInUse");
            return;
        }

        var shortcuts = _preferences.CaptureShortcuts is { } saved
            ? new Dictionary<int, TrayShortcut>(saved) : [];
        shortcuts[id] = shortcut;
        if (!TrySavePreferences(_preferences with { CaptureShortcuts = shortcuts }))
        {
            if (register && _windowSource is { } source)
                Native.UnregisterHotKey(source.Handle, replacement);
            ShowCaptureShortcutStatus("SettingsWriteFailure");
            return;
        }

        if (register)
        {
            if (existing != 0 && _windowSource is { } source)
                Native.UnregisterHotKey(source.Handle, existing);
            _registered[id] = replacement;
        }
        StopRecordingCaptureShortcut();
        UpdateHotkeyLabels();
        Report(UiText.Get("CaptureShortcutSaved"));
    }

    private void RefreshCaptureShortcuts()
    {
        foreach (var (id, button) in _captureShortcutButtons)
        {
            if (_recordingCaptureShortcut == id)
                button.SetResourceReference(ContentControl.ContentProperty, UiText.Key("WorkspaceShortcutListening"));
            else
                button.Content = CaptureGesture(id).Display() +
                    (_preferences.Hotkeys && !_registered.ContainsKey(id) ? " " + UiText.Get("ShortcutOccupied") : "");
        }
    }

    private void StopRecordingCaptureShortcut()
    {
        _recordingCaptureShortcut = null;
        CaptureShortcutStatus.Visibility = Visibility.Collapsed;
        RefreshCaptureShortcuts();
    }

    private void ShowCaptureShortcutStatus(string key)
    {
        CaptureShortcutStatus.Visibility = Visibility.Visible;
        CaptureShortcutStatus.SetResourceReference(TextBlock.TextProperty, UiText.Key(key));
    }
}
