using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Framelet;

public partial class MainWindow
{
    private const int FirstWorkspaceHotkeyId = 8110;
    private const int SecondWorkspaceHotkeyId = 8111;
    private int? _workspaceHotkeyId;
    private bool _recordingWorkspaceShortcut;

    private TrayShortcut WorkspaceGesture => _preferences.WorkspaceShortcut is { IsUsable: true } saved && !IsCaptureShortcut(saved)
        ? saved : TrayShortcut.Default;

    private static bool IsCaptureShortcut(TrayShortcut shortcut) => shortcut.NativeModifiers == Native.ControlShift
        && (shortcut.VirtualKey == 0x35 || Shortcuts.Any(item => item.Key == shortcut.VirtualKey));

    private void RegisterWorkspaceHotkey()
    {
        if (_preferences.Hotkeys && RegisterWorkspaceHotkey(FirstWorkspaceHotkeyId, WorkspaceGesture))
            _workspaceHotkeyId = FirstWorkspaceHotkeyId;
    }

    private bool RegisterWorkspaceHotkey(int id, TrayShortcut shortcut) => _windowSource is { } source
        && Native.RegisterHotKey(source.Handle, id, shortcut.NativeModifiers, shortcut.VirtualKey);

    private void UnregisterWorkspaceHotkey()
    {
        if (_workspaceHotkeyId is { } id && _windowSource is { } source)
            Native.UnregisterHotKey(source.Handle, id);
        _workspaceHotkeyId = null;
    }

    private void WorkspaceShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (_recordingWorkspaceShortcut)
        {
            StopRecordingWorkspaceShortcut();
            return;
        }

        _recordingWorkspaceShortcut = true;
        WorkspaceShortcutButton.Focus();
        RefreshWorkspaceShortcut();
    }

    private void StopRecordingWorkspaceShortcut()
    {
        _recordingWorkspaceShortcut = false;
        RefreshWorkspaceShortcut();
    }

    private void RecordWorkspaceShortcut(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.IsRepeat)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecordingWorkspaceShortcut();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        TryChangeWorkspaceShortcut(new TrayShortcut(key, Keyboard.Modifiers));
    }

    private void TryChangeWorkspaceShortcut(TrayShortcut shortcut)
    {
        if (!shortcut.IsUsable || IsCaptureShortcut(shortcut))
        {
            ShowWorkspaceShortcutStatus(shortcut.IsUsable ? "WorkspaceShortcutInUse" : "WorkspaceShortcutInvalid");
            return;
        }
        if (shortcut == WorkspaceGesture && (!_preferences.Hotkeys || _workspaceHotkeyId is not null))
        {
            StopRecordingWorkspaceShortcut();
            return;
        }

        var replacement = _workspaceHotkeyId == FirstWorkspaceHotkeyId ? SecondWorkspaceHotkeyId : FirstWorkspaceHotkeyId;
        var shouldRegister = _preferences.Hotkeys;
        if (shouldRegister && !RegisterWorkspaceHotkey(replacement, shortcut))
        {
            ShowWorkspaceShortcutStatus("WorkspaceShortcutInUse");
            return;
        }

        if (!TrySavePreferences(_preferences with { WorkspaceShortcut = shortcut }))
        {
            if (shouldRegister && _windowSource is { } source)
                Native.UnregisterHotKey(source.Handle, replacement);
            ShowWorkspaceShortcutStatus("SettingsWriteFailure");
            return;
        }

        UnregisterWorkspaceHotkey();
        _workspaceHotkeyId = shouldRegister ? replacement : null;
        StopRecordingWorkspaceShortcut();
        UpdateTrayShortcuts();
        Report(UiText.Get("WorkspaceShortcutSaved"));
    }

    private void RefreshWorkspaceShortcut()
    {
        if (_recordingWorkspaceShortcut)
        {
            WorkspaceShortcutButton.SetResourceReference(ContentControl.ContentProperty, UiText.Key("WorkspaceShortcutListening"));
            ShowWorkspaceShortcutStatus("WorkspaceShortcutRecorderHint");
            return;
        }

        WorkspaceShortcutButton.Content = WorkspaceGesture.Display();
        if (_preferences.Hotkeys && _workspaceHotkeyId is null)
            ShowWorkspaceShortcutStatus("WorkspaceShortcutInUse");
        else
            WorkspaceShortcutStatus.Visibility = Visibility.Collapsed;
    }

    private void ShowWorkspaceShortcutStatus(string key)
    {
        WorkspaceShortcutStatus.Visibility = Visibility.Visible;
        WorkspaceShortcutStatus.SetResourceReference(TextBlock.TextProperty, UiText.Key(key));
    }

    private bool InterceptShortcutWhileRecording(int id)
    {
        if (!_recordingWorkspaceShortcut || !IsActive || SettingsLayer.Visibility != Visibility.Visible)
            return false;
        if (id == _workspaceHotkeyId)
            StopRecordingWorkspaceShortcut();
        else if (id == LastCaptureShortcutId || Shortcuts.Any(shortcut => shortcut.Id == id))
            ShowWorkspaceShortcutStatus("WorkspaceShortcutInUse");
        else
            return false;
        return true;
    }

    private void ToggleWorkspace()
    {
        if (_busy)
            return;
        if (IsVisible && IsActive && WindowState != WindowState.Minimized)
            Hide();
        else
            ShowWorkspace();
    }
}
