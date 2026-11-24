using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Framelet;

public partial class MainWindow
{
    private CancellationTokenSource? _ocrRequest;
    private string _ocrLanguage = OcrService.AutomaticMode;
    private int _scannedOcrRevision = -1;
    private Button? _ocrCopiedButton;
    private int _ocrCopyVersion;
    private bool _copyWhenOcrReady;
    private IReadOnlyList<Rect> _contactRegions = [];
    private RecognizedImage? _ocrResult;

    private async void Ocr_Click(object sender, RoutedEventArgs e) => await OpenOcrAsync();
    private async void RefreshOcr_Click(object sender, RoutedEventArgs e) => await RecognizeOcrAsync();
    private void CloseOcr_Click(object sender, RoutedEventArgs e) => CloseOcr();

    private async Task OpenOcrAsync()
    {
        EditorImage.CommitTextInput();
        OcrPanel.Visibility = Visibility.Visible;
        OcrImageModeToggle.IsChecked = true;
        SetSelected(OcrButton, true, accentText: true);
        if (_ocrRequest is not null || OcrResults.Visibility == Visibility.Visible)
        {
            UpdateOcrImageMode();
            return;
        }

        await RecognizeOcrAsync();
    }

    private async void OcrLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string language } && language != _ocrLanguage)
        {
            _ocrLanguage = language;
            await RecognizeOcrAsync();
        }
    }

    private async Task RecognizeOcrAsync()
    {
        CancelOcrRequest();
        foreach (var button in OcrLanguages.Children.OfType<Button>())
            SetSelected(button, (string)button.Tag == _ocrLanguage, accentText: true);

        using var request = new CancellationTokenSource();
        _ocrRequest = request;
        _scannedOcrRevision = EditorImage.OcrRevision;
        ClearOcrResults();
        SetOcrStatus("OcrWorking");
        OcrProgress.Visibility = Visibility.Visible;

        try
        {
            var image = await EditorImage.ExportForOcrAsync();
            var result = await OcrService.RecognizeAsync(image, _ocrLanguage, request.Token);
            if (_ocrRequest != request)
                return;

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                SetOcrStatus("OcrNoText");
                RefreshOcrButton.Visibility = Visibility.Visible;
                return;
            }

            OcrStatus.Visibility = Visibility.Collapsed;
            OcrText.Text = result.Text;
            _ocrResult = result;
            UpdateOcrFields();
            _contactRegions = result.ContactRegions;
            OcrRedactActions.Visibility = _contactRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            OcrResults.Visibility = Visibility.Visible;
            OcrCanvasText.SetLayout(result.Layout);
            UpdateOcrImageMode();
            if (_copyWhenOcrReady && OcrCanvasText.IsActive && OcrCanvasText.HasSelection)
                await CopyOcrSelectionAsync();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            if (_ocrRequest == request)
            {
                SetOcrStatus(ex.GetBaseException() is DllNotFoundException ? "OcrNativeFailure" : "OcrFailure");
                RefreshOcrButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (_ocrRequest == request)
            {
                _ocrRequest = null;
                _copyWhenOcrReady = false;
                OcrProgress.Visibility = Visibility.Collapsed;
                UpdateOcrImageMode();
            }
        }
    }

    private void InvalidateOcr()
    {
        if (OcrPanel.Visibility != Visibility.Visible || _scannedOcrRevision == EditorImage.OcrRevision)
            return;

        CancelOcrRequest();
        ClearOcrResults();
        SetOcrStatus("OcrChanged");
        RefreshOcrButton.Visibility = Visibility.Visible;
    }

    private void CloseOcr()
    {
        if (OcrPanel.Visibility != Visibility.Visible)
            return;

        var returnFocus = OcrPanel.IsKeyboardFocusWithin;
        CancelOcrRequest();
        ClearOcrResults();
        OcrPanel.Visibility = Visibility.Collapsed;
        OcrButton.ClearValue(ForegroundProperty);
        OcrButton.ClearValue(BackgroundProperty);
        OcrButton.ClearValue(BorderBrushProperty);
        if (returnFocus)
            EditorImage.Focus();
    }

    private void CancelOcrRequest()
    {
        var previous = _ocrRequest;
        _ocrRequest = null;
        _copyWhenOcrReady = false;
        previous?.Cancel();
    }

    private void ClearOcrResults()
    {
        _ocrResult = null;
        _contactRegions = [];
        OcrRedactActions.Visibility = Visibility.Collapsed;
        ClearOcrCopyFeedback();
        OcrCanvasText.SetLayout(null);
        UpdateOcrImageMode();
        OcrStatus.Visibility = Visibility.Visible;
        OcrProgress.Visibility = Visibility.Collapsed;
        RefreshOcrButton.Visibility = Visibility.Collapsed;
        OcrResults.Visibility = Visibility.Collapsed;
        OcrContacts.ItemsSource = null;
        OcrText.Clear();
    }

    private void UpdateOcrFields()
    {
        if (_ocrResult is not { } result)
            return;

        ClearOcrCopyFeedback();
        OcrCanvasText.SetHighlightedRegions([]);
        var customFields = OcrService.ReadTemplateFields(result.Layout, _preferences.OcrTemplates ?? []);
        OcrContacts.ItemsSource = result.Contacts.Concat(customFields).ToArray();
        OcrContactsBlock.Visibility = result.Contacts.Count + customFields.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OcrTemplates_Click(object sender, RoutedEventArgs e)
    {
        OcrTemplatesButton.IsEnabled = false;
        try
        {
            var result = _ocrResult;
            var image = result is null ? null : await EditorImage.ExportForOcrAsync();
            if (!IsVisible || OcrPanel.Visibility != Visibility.Visible)
                return;
            if (!ReferenceEquals(result, _ocrResult))
            {
                result = null;
                image = null;
            }

            var dialog = new OcrTemplatesWindow(_preferences.OcrTemplates ?? [], result?.Layout, image) { Owner = this };
            if (dialog.ShowDialog() == true && TrySavePreferences(_preferences with { OcrTemplates = dialog.Templates }))
                UpdateOcrFields();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            Report(UiText.Get("OcrTemplatesFailure"), true);
        }
        finally
        {
            OcrTemplatesButton.IsEnabled = true;
        }
    }

    private void RedactOcrContacts_Click(object sender, RoutedEventArgs e)
    {
        if (_contactRegions.Count == 0 || _scannedOcrRevision != EditorImage.OcrRevision)
            return;

        LeaveOcrImageMode();
        EditorImage.RedactContacts(_contactRegions);
        EditorImage.DeselectAnnotation();
        EditorImage.Focus();
        SetOcrStatus("OcrContactsHidden");
        Report(UiText.Get("OcrContactsHidden"));
    }

    private async void CopyOcrText_Click(object sender, RoutedEventArgs e)
    {
        await CopyRecognizedAsync(OcrCopyAll, OcrText.Text, UiText.Get("OcrTextCopied"));
    }

    private void OcrImageMode_Click(object sender, RoutedEventArgs e) => UpdateOcrImageMode();

    private async void CopyOcrSelection_Click(object sender, RoutedEventArgs e) => await CopyOcrSelectionAsync();

    private Task CopyOcrSelectionAsync()
        => CopyRecognizedAsync(OcrCopySelectionButton, OcrCanvasText.SelectedText, UiText.Get("OcrTextCopied"));

    private Task CopyOcrShortcutAsync()
    {
        if (OcrCanvasText.HasSelection)
            return CopyOcrSelectionAsync();

        _copyWhenOcrReady = OcrCanvasText.HasPendingSelection;
        return Task.CompletedTask;
    }

    private void LeaveOcrImageMode()
    {
        if (OcrImageModeToggle.IsChecked != true)
            return;

        OcrImageModeToggle.IsChecked = false;
        _copyWhenOcrReady = false;
        UpdateOcrImageMode();
    }

    private void UpdateOcrImageMode()
    {
        var available = OcrCanvasText.HasWords || _ocrRequest is not null;
        var active = available && OcrImageModeToggle.IsChecked == true;
        OcrImageControls.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (active && !OcrCanvasText.IsActive)
        {
            StopPanning();
            EditorImage.CancelDraft();
            EditorImage.DeselectAnnotation();
        }
        else if (!active)
        {
            _copyWhenOcrReady = false;
            if (OcrCanvasText.IsKeyboardFocusWithin)
                EditorImage.Focus();
            OcrCanvasText.ClearSelection();
        }
        OcrCanvasText.SetActive(active);
        UpdateOcrSelectionAction();
        UpdateEditorToolState();
    }

    private void UpdateOcrSelectionAction()
    {
        OcrCopySelectionButton.IsEnabled = OcrCanvasText.IsActive && OcrCanvasText.HasSelection;
    }

    private async void OcrContact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RecognizedContact contact } button)
            await CopyRecognizedAsync(button, contact.Value, UiText.Format("CopiedContact", contact.Label));
    }

    private void OcrContact_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button { DataContext: RecognizedContact field })
            OcrCanvasText.SetHighlightedRegions(field.Regions);
    }

    private void OcrContact_MouseLeave(object sender, MouseEventArgs e)
        => OcrCanvasText.SetHighlightedRegions([]);

    private async Task CopyRecognizedAsync(Button button, string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            await AccessClipboardAsync(() => Clipboard.SetText(text));
            Report(message);
            if (OcrResults.Visibility == Visibility.Visible && button.IsVisible)
                _ = ShowOcrCopyFeedbackAsync(button);
        }
        catch (ExternalException ex)
        {
            Trace.WriteLine(ex);
            Report(UiText.Get("WindowsOperationFailure"), true);
        }
    }

    private async Task ShowOcrCopyFeedbackAsync(Button button)
    {
        ClearOcrCopyFeedback();
        var version = ++_ocrCopyVersion;
        _ocrCopiedButton = button;
        button.Tag = "Copied";
        await Task.Delay(1400);
        if (version == _ocrCopyVersion)
            ClearOcrCopyFeedback();
    }

    private void ClearOcrCopyFeedback()
    {
        if (_ocrCopiedButton is not null)
            _ocrCopiedButton.Tag = null;
        _ocrCopiedButton = null;
    }

    private void SetOcrStatus(string key) => OcrStatus.SetResourceReference(TextBlock.TextProperty, UiText.Key(key));

    private void RefreshOcrContactLabels()
    {
        ClearOcrCopyFeedback();
        OcrCanvasText.SetHighlightedRegions([]);
        if (OcrContacts.ItemsSource is { } contacts)
        {
            OcrContacts.ItemsSource = null;
            OcrContacts.ItemsSource = contacts;
        }
    }
}
