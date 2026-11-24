using System.Diagnostics;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Framelet;

public partial class MainWindow
{
    private enum SettingsTab { General, Ocr, OcrTemplates }

    private void SettingsTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string name } && Enum.TryParse<SettingsTab>(name, out var tab))
            SelectSettingsTab(tab);
    }

    private void SelectSettingsTab(SettingsTab tab)
    {
        if (tab != SettingsTab.General)
        {
            StopRecordingWorkspaceShortcut();
            StopRecordingCaptureShortcut();
        }
        if (tab == SettingsTab.Ocr)
            RefreshOcrModels();
        if (tab == SettingsTab.OcrTemplates)
            RefreshOcrTemplatesSettings();

        GeneralSettingsTab.IsChecked = tab == SettingsTab.General;
        OcrSettingsTab.IsChecked = tab == SettingsTab.Ocr;
        OcrTemplatesSettingsTab.IsChecked = tab == SettingsTab.OcrTemplates;
        GeneralSettingsPage.Visibility = tab == SettingsTab.General ? Visibility.Visible : Visibility.Collapsed;
        OcrSettingsPage.Visibility = tab == SettingsTab.Ocr ? Visibility.Visible : Visibility.Collapsed;
        OcrTemplatesSettingsPage.Visibility = tab == SettingsTab.OcrTemplates ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshOcrTemplatesSettings()
    {
        var templates = _preferences.OcrTemplates ?? [];
        SettingsOcrTemplates.ItemsSource = templates;
        SettingsOcrTemplatesEmpty.Visibility = templates.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshStartupSetting()
    {
        try
        {
            StartupSetting.IsChecked = LoginStartup.IsEnabled();
            StartupSetting.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            Trace.WriteLine(ex);
            StartupSetting.IsChecked = false;
            StartupSetting.IsEnabled = false;
            Report(UiText.Get("StartupError"), true);
        }
    }

    private void StartupSetting_Click(object sender, RoutedEventArgs e)
    {
        var enabled = StartupSetting.IsChecked == true;
        try
        {
            LoginStartup.SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            Trace.WriteLine(ex);
            StartupSetting.IsChecked = !enabled;
            Report(UiText.Get("StartupError"), true);
        }
    }

    private async void ChooseStorageFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = UiText.Get("ChooseStorageFolder"),
            InitialDirectory = Directory.Exists(_history.Folder) ? _history.Folder : "",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
            await SetStorageFolderAsync(dialog.FolderName);
    }

    private async void ResetStorageFolder_Click(object sender, RoutedEventArgs e)
        => await SetStorageFolderAsync(_history.DefaultFolder);

    private Task SetStorageFolderAsync(string path)
    {
        return RunOperation(async () =>
        {
            await EnsureHistoryLoadedAsync();
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(folder, _history.Folder, StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $".framelet-{Guid.NewGuid():N}.tmp");
                using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose);
            });

            var rootPaths = (_preferences.HistoryFolders ?? []).Append(_history.Folder).Append(folder)
                .Where(value => !string.Equals(value, _history.DefaultFolder, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var desired = _preferences with
            {
                StorageFolder = string.Equals(folder, _history.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : folder,
                HistoryFolders = rootPaths
            };
            if (!TrySavePreferences(desired))
                return;

            _history.ConfigureStorage(_preferences);
            RefreshStorageSetting();
            RefreshHistory(await Task.Run(_history.Load));
            Report(UiText.Get("StorageFolderChanged"));
        });
    }

    private void RefreshStorageSetting()
    {
        StorageFolderPath.Text = _history.Folder;
        ResetStorageFolderButton.IsEnabled = !string.Equals(_history.Folder, _history.DefaultFolder, StringComparison.OrdinalIgnoreCase);
    }
}
