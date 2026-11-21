using System.Windows;
using System.Windows.Input;

namespace Framelet;

public partial class MainWindow
{
    private bool IsCropping => CropLayer.Visibility == Visibility.Visible;

    private void Crop_Click(object sender, RoutedEventArgs e) => BeginCrop();
    private void ApplyCrop_Click(object sender, RoutedEventArgs e) => ApplyCrop();
    private void CancelCrop_Click(object sender, RoutedEventArgs e) => EndCrop();

    private void BeginCrop()
    {
        if (_busy || IsCropping || !EditorView.IsEnabled || double.IsNaN(EditorImage.Width))
            return;

        CloseOcr();
        StopPanning();
        EditorImage.CommitTextInput();
        EditorImage.CompleteStrokeAdjustment();
        EditorImage.CancelDraft();
        EditorImage.DeselectAnnotation();
        EditorHeaderActions.IsEnabled = EditorToolsPanel.IsEnabled = false;
        EditorFooter.Visibility = Visibility.Collapsed;
        CropActions.Visibility = CropLayer.Visibility = Visibility.Visible;
        CropLayer.Begin(EditorImage.Width, EditorImage.Height);
        UpdateCropSelection();
    }

    private void UpdateCropSelection()
    {
        var selection = CropLayer.PixelSelection;
        CropSize.Text = $"{selection.Width} × {selection.Height} px";
        CropApplyButton.IsEnabled = CropLayer.HasCrop;
    }

    private void ApplyCrop()
    {
        if (!IsCropping || !CropLayer.HasCrop)
            return;

        var selection = CropLayer.PixelSelection;
        EndCrop();
        _fit = true;
        if (EditorImage.Crop(selection))
            Report(UiText.Get("CropApplied"));
    }

    private void EndCrop(bool returnFocus = true)
    {
        if (!IsCropping)
            return;

        CropLayer.End();
        CropLayer.Visibility = CropActions.Visibility = Visibility.Collapsed;
        EditorFooter.Visibility = Visibility.Visible;
        EditorHeaderActions.IsEnabled = EditorToolsPanel.IsEnabled = true;
        if (returnFocus)
            EditorImage.Focus();
    }

    private bool HandleCropKey(KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.Space)
            return false;

        if (e.Key == Key.Enter)
            ApplyCrop();
        else if (e.Key == Key.Escape)
            EndCrop();
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D0)
        {
            _fit = true;
            ApplyFit();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D1)
            SetManualZoom(1);

        e.Handled = true;
        return true;
    }
}
