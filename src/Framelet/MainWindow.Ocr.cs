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
    private Rect[] _ocrFieldRegions = [];
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
        OcrLanguagePicker.IsOpen = false;
        OcrLanguageButton.Focus();
        if (sender is Button { Tag: string language } && language != _ocrLanguage)
        {
            _ocrLanguage = language;
            UpdateOcrLanguageLabel();
            await RecognizeOcrAsync();
        }
    }

    private async Task RecognizeOcrAsync()
    {
        CancelOcrRequest();
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
                SetOcrStatus(ex switch
                {
                    UserFacingException known => known.ResourceKey,
                    _ when ex.GetBaseException() is DllNotFoundException => "OcrNativeFailure",
                    _ => "OcrFailure"
                });
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
        OcrLanguagePicker.IsOpen = false;
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
        _ocrFieldRegions = [];
        OcrHighlightAll.IsChecked = false;
        OcrHighlightAll.IsEnabled = false;
        OcrRedactOptions.IsChecked = false;
        OcrRedactGroups.Children.Clear();
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
        var customFields = OcrService.ReadTemplateFields(result.Layout, _preferences.OcrTemplates ?? []);
        var fields = result.Contacts.Concat(customFields).ToArray();
        OcrContacts.ItemsSource = fields;
        OcrContactsBlock.Visibility = fields.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ocrFieldRegions = fields.SelectMany(field => field.Regions).Distinct().ToArray();
        OcrHighlightAll.IsEnabled = _ocrFieldRegions.Length > 0;
        OcrRedactOptions.IsEnabled = OcrHighlightAll.IsEnabled;
        if (_ocrFieldRegions.Length == 0)
        {
            OcrHighlightAll.IsChecked = false;
            OcrRedactOptions.IsChecked = false;
        }
        SetFieldHighlight();
        BuildRedactionOptions(fields);
    }

    private async void OcrTemplates_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { IsEnabled: true } entry)
            return;

        OcrTemplatesButton.IsEnabled = OpenSettingsOcrTemplates.IsEnabled = false;
        try
        {
            var result = _ocrResult;
            var image = result is null ? null : await EditorImage.ExportForOcrAsync();
            if (!IsVisible || !entry.IsVisible)
                return;
            if (!ReferenceEquals(result, _ocrResult))
            {
                result = null;
                image = null;
            }

            var dialog = new OcrTemplatesWindow(_preferences.OcrTemplates ?? [], result?.Layout, image) { Owner = this };
            if (dialog.ShowDialog() == true && TrySavePreferences(_preferences with { OcrTemplates = dialog.Templates }))
            {
                RefreshOcrTemplatesSettings();
                UpdateOcrFields();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            Report(UiText.Get("OcrTemplatesFailure"), true);
        }
        finally
        {
            OcrTemplatesButton.IsEnabled = OpenSettingsOcrTemplates.IsEnabled = true;
        }
    }

    private void BuildRedactionOptions(IEnumerable<RecognizedContact> fields)
    {
        OcrRedactGroups.Children.Clear();
        foreach (var group in fields.Where(field => field.Regions.Count > 0).GroupBy(field => (field.Kind, field.TemplateName)))
        {
            var choice = new CheckBox
            {
                Tag = new RedactionChoice(group.Key.Kind, group.SelectMany(field => field.Regions).Distinct().ToArray()),
                IsChecked = group.Key.Kind is ContactKind.Email or ContactKind.Phone or ContactKind.Address or ContactKind.Location,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            };
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            if (group.Key.TemplateName is { } name)
                label.Text = name;
            else
                label.SetResourceReference(TextBlock.TextProperty, UiText.Key(group.Key.Kind.ToString()));
            choice.Content = label;
            choice.Checked += OcrRedactChoice_Changed;
            choice.Unchecked += OcrRedactChoice_Changed;
            OcrRedactGroups.Children.Add(choice);
        }
        OcrChooseNames.IsEnabled = RedactionChoices.Any(choice => ((RedactionChoice)choice.Tag).Kind == ContactKind.PersonName);
        UpdateRedactButton();
    }

    private IEnumerable<CheckBox> RedactionChoices => OcrRedactGroups.Children.OfType<CheckBox>();

    private void OcrRedactChoice_Changed(object sender, RoutedEventArgs e) => UpdateRedactButton();

    private void UpdateRedactButton() => OcrApplyRedaction.IsEnabled = RedactionChoices.Any(choice => choice.IsChecked == true);

    private void ChooseNames_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in RedactionChoices)
            choice.IsChecked = ((RedactionChoice)choice.Tag).Kind == ContactKind.PersonName;
    }

    private void ChooseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in RedactionChoices)
            choice.IsChecked = true;
    }

    private void ApplyOcrRedaction_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrResult is null || _scannedOcrRevision != EditorImage.OcrRevision)
            return;
        var regions = RedactionChoices.Where(choice => choice.IsChecked == true)
            .SelectMany(choice => ((RedactionChoice)choice.Tag).Regions).Distinct().ToArray();
        if (regions.Length == 0)
            return;

        LeaveOcrImageMode();
        EditorImage.RedactContacts(regions);
        EditorImage.DeselectAnnotation();
        EditorImage.Focus();
        SetOcrStatus("OcrFieldsHidden");
        Report(UiText.Get("OcrFieldsHidden"));
    }

    private sealed record RedactionChoice(ContactKind Kind, Rect[] Regions);

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
            SetFieldHighlight(field);
    }

    private void OcrContact_MouseLeave(object sender, MouseEventArgs e)
        => SetFieldHighlight();

    private void OcrHighlightAll_Click(object sender, RoutedEventArgs e) => SetFieldHighlight();

    private void SetFieldHighlight(RecognizedContact? field = null) =>
        OcrCanvasText.SetHighlightedRegions(OcrHighlightAll.IsChecked == true ? _ocrFieldRegions : field?.Regions ?? []);

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
        SetFieldHighlight();
        if (OcrContacts.ItemsSource is { } contacts)
        {
            OcrContacts.ItemsSource = null;
            OcrContacts.ItemsSource = contacts;
        }
    }
}
