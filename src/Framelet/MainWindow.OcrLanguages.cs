using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Framelet;

public partial class MainWindow
{
    private CancellationTokenSource? _ocrDownload;
    private string? _failedOcrDownload;
    private OcrLanguage[] _installedOcrLanguages = [];
    private OcrLanguage[] _availableOcrLanguages = [];

    private void UpdateOcrLanguageLabel()
    {
        var label = _ocrLanguage == OcrService.AutomaticMode
            ? UiText.Get("OcrAuto")
            : OcrLanguages.Find(_ocrLanguage)?.DisplayName ?? _ocrLanguage;
        OcrLanguageLabel.Text = label;
        OcrLanguageButton.ToolTip = _ocrLanguage == OcrService.AutomaticMode
            ? string.Join(", ", OcrService.AutomaticLanguages.Split('+').Select(code => OcrLanguages.Find(code)!.DisplayName))
            : label;
    }

    private void OpenOcrLanguagePicker_Click(object sender, RoutedEventArgs e)
    {
        _installedOcrLanguages = OcrLanguages.Catalog.Where(OcrLanguages.IsInstalled)
            .OrderBy(language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        OcrPickerSearch.Clear();
        PopulateOcrPicker();
        OcrLanguagePicker.IsOpen = true;
    }

    private void OcrLanguagePicker_Opened(object sender, EventArgs e) => OcrPickerSearch.Focus();

    private void OcrPickerSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (OcrPickerLanguages is not null)
            PopulateOcrPicker();
    }

    private void PopulateOcrPicker()
    {
        var query = OcrPickerSearch.Text.Trim();
        OcrPickerLanguages.Children.Clear();
        var automatic = UiText.Get("OcrAuto");
        if (automatic.Contains(query, StringComparison.CurrentCultureIgnoreCase) || query.Equals("auto", StringComparison.OrdinalIgnoreCase))
            AddChoice(OcrService.AutomaticMode, automatic);
        foreach (var model in _installedOcrLanguages.Where(language => language.Matches(query)))
            AddChoice(model.Code, model.DisplayName);

        void AddChoice(string code, string name)
        {
            var button = new Button
            {
                Tag = code,
                Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = name,
                Style = (Style)FindResource("GhostButton"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 5, 8, 5)
            };
            SetSelected(button, code == _ocrLanguage, accentText: true);
            button.Click += OcrLanguage_Click;
            OcrPickerLanguages.Children.Add(button);
        }
    }

    private void OcrLanguagePicker_Closed(object sender, EventArgs e)
    {
        if (OcrPickerSearch.IsKeyboardFocusWithin || OcrPickerLanguages.IsKeyboardFocusWithin)
            OcrLanguageButton.Focus();
    }

    private void OcrLanguagePicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        OcrLanguagePicker.IsOpen = false;
        OcrLanguageButton.Focus();
    }

    private void ManageOcrLanguages_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings(SettingsTab.Ocr);
    }

    private void RefreshOcrModels()
    {
        var ordered = OcrLanguages.Catalog.OrderBy(model => model.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _installedOcrLanguages = ordered.Where(OcrLanguages.IsInstalled).ToArray();
        _availableOcrLanguages = ordered.Except(_installedOcrLanguages).ToArray();
        var active = OcrService.AutomaticLanguages.Split('+');
        OcrAutoModels.Children.Clear();
        foreach (var model in _installedOcrLanguages)
        {
            var choice = new CheckBox
            {
                Content = new TextBlock { Text = model.DisplayName, TextWrapping = TextWrapping.Wrap, MaxWidth = 415 },
                Tag = model.Code,
                IsChecked = active.Contains(model.Code),
                Margin = new Thickness(0, 0, 15, 8),
                FontSize = 13
            };
            choice.Click += OcrAutomaticLanguage_Click;
            OcrAutoModels.Children.Add(choice);
        }
        PopulateOcrCatalog();
    }

    private async void OcrAutomaticLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string code } choice)
            return;
        OcrAutoStatus.Text = "";
        var current = OcrService.AutomaticLanguages.Split('+');
        var desired = choice.IsChecked == true ? current.Append(code).ToArray() : current.Where(value => value != code).ToArray();
        if (desired.Length == 0 || !TrySavePreferences(_preferences with { AutomaticOcrLanguages = desired }))
        {
            choice.IsChecked = current.Contains(code);
            if (desired.Length == 0)
                OcrAutoStatus.Text = UiText.Get("OcrKeepOneLanguage");
            return;
        }

        OcrService.ConfigureAutomaticLanguages(desired);
        UpdateOcrLanguageLabel();
        var previous = _historyIndexRequest;
        _historyIndexRequest = null;
        previous?.Cancel();
        _historyText.Clear();
        _historyIndexUnavailable = false;
        FilterHistory();
        UpdateHistoryIndex();
        if (_ocrLanguage == OcrService.AutomaticMode && OcrPanel.Visibility == Visibility.Visible)
            await RecognizeOcrAsync();
    }

    private void OcrCatalogSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (OcrCatalog is not null)
            PopulateOcrCatalog();
    }

    private void PopulateOcrCatalog()
    {
        var query = OcrCatalogSearch.Text.Trim();
        var selected = OcrCatalog.SelectedItem;
        OcrCatalog.ItemsSource = _availableOcrLanguages.Where(language => language.Matches(query)).ToArray();
        OcrCatalog.SelectedItem = selected;
        OcrCatalogEmpty.Visibility = OcrCatalog.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateOcrInstallAction();
    }

    private void OcrCatalog_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOcrInstallAction();

    private void UpdateOcrInstallAction()
    {
        if (OcrInstallLanguage is null)
            return;
        var selection = OcrCatalog.SelectedItem as OcrLanguage;
        var downloading = _ocrDownload is not null;
        OcrInstallLanguage.IsEnabled = selection is not null && !downloading;
        OcrInstallLanguage.SetResourceReference(ContentControl.ContentProperty, UiText.Key(
            selection is not null && selection.Code == _failedOcrDownload ? "OcrDownloadRetry" : "OcrInstallLanguage"));
        OcrCatalog.IsEnabled = OcrCatalogSearch.IsEnabled = !downloading;
        OcrModelSize.Text = selection is null ? "" : UiText.Format("OcrDownloadSize", selection.DownloadSize);
        OcrCancelDownload.Visibility = OcrDownloadProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OcrCancelDownload_Click(object sender, RoutedEventArgs e) => _ocrDownload?.Cancel();

    private async void OcrInstallLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrDownload is not null || OcrCatalog.SelectedItem is not OcrLanguage model)
            return;

        using var request = new CancellationTokenSource();
        _ocrDownload = request;
        _failedOcrDownload = null;
        OcrDownloadProgress.Value = 0;
        UpdateOcrInstallAction();
        OcrDownloadStatus.Text = UiText.Format("OcrDownloadWorking", model.DisplayName);
        var progress = new Progress<int>(bytes =>
        {
            if (_ocrDownload != request || request.IsCancellationRequested)
                return;
            var percent = (int)(100L * bytes / model.Size);
            OcrDownloadProgress.Value = percent;
            OcrDownloadStatus.Text = UiText.Format("OcrDownloadProgress", percent, bytes / 1_048_576d, model.Size / 1_048_576d);
        });
        try
        {
            await OcrLanguages.InstallAsync(model, progress, request.Token);
            OcrDownloadStatus.Text = UiText.Format("OcrDownloadSuccess", model.DisplayName);
            RefreshOcrModels();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            OcrDownloadStatus.Text = "";
        }
        catch (TimeoutException)
        {
            _failedOcrDownload = model.Code;
            OcrDownloadStatus.Text = UiText.Get("OcrDownloadStalled");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            Trace.WriteLine(ex);
            _failedOcrDownload = model.Code;
            OcrDownloadStatus.Text = UiText.Get("OcrDownloadFailure");
        }
        finally
        {
            _ocrDownload = null;
            UpdateOcrInstallAction();
        }
    }
}
