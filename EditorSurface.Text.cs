using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Framelet;

public sealed partial class EditorSurface
{
    private static readonly Typeface AnnotationTypeface = new(new FontFamily("Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush DarkTextShadow = (Brush)new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)).GetAsFrozen();
    private static readonly Brush LightTextShadow = (Brush)new SolidColorBrush(Color.FromArgb(135, 255, 255, 255)).GetAsFrozen();
    private AnnotationTextDraft? _textInput;
    private (Annotation Mark, int Index)? _pendingTextClick;

    internal bool IsEditingText => _textInput is not null;

    internal bool EditSelectedText()
    {
        if (SelectedMark is not { Tool: EditorTool.Text } mark)
            return false;

        CompleteStrokeAdjustment();
        BeginTextInput(mark, _selectedIndex);
        return true;
    }

    internal void CommitTextInput() => FinishTextInput(TextInputCompletion.Dismiss);

    private void StartTextClick(Annotation mark, int index = -1)
    {
        if (CaptureMouse())
            _pendingTextClick = (mark, index);
    }

    private void BeginTextInput(Annotation mark, int index = -1)
    {
        if (_textInput is not null)
            return;

        var field = new TextBox
        {
            Text = mark.Label ?? "", FontFamily = AnnotationTypeface.FontFamily, FontWeight = AnnotationTypeface.Weight,
            FontSize = Math.Clamp(AnnotationTextSize(mark) * _zoom, 16, 26), MaxLength = 450,
            MaxHeight = 125, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6)
        };
        AutomationProperties.SetName(field, UiText.Get("AnnotationText"));
        var add = new Button
        {
            Content = UiText.Get(index < 0 ? "Add" : "Save"), ToolTip = "Enter",
            Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(10, 3, 10, 3)
        };
        add.Click += (_, _) => FinishTextInput(TextInputCompletion.Confirm, returnFocus: true);
        var cancel = new Button
        {
            Content = "×", ToolTip = $"{UiText.Get("Cancel")} · Esc", Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(3, 0, 0, 0)
        };
        AutomationProperties.SetName(cancel, UiText.Get("Cancel"));
        cancel.Click += (_, _) => FinishTextInput(TextInputCompletion.Cancel, returnFocus: true);
        var actions = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(add, Dock.Right);
        actions.Children.Add(cancel);
        actions.Children.Add(add);
        actions.Children.Add(new TextBlock
        {
            Text = UiText.Get("AnnotationNewLine"), Foreground = (Brush)FindResource("Muted"),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 6, 0)
        });
        var content = new StackPanel();
        content.Children.Add(field);
        content.Children.Add(actions);
        content.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            FinishTextInput(TextInputCompletion.Cancel, returnFocus: true);
        };
        field.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers == ModifierKeys.Shift)
                return;
            e.Handled = true;
            FinishTextInput(TextInputCompletion.Confirm, returnFocus: true);
        };

        var point = mark.Points[0] - new Vector(ViewX, ViewY);
        var popup = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.RelativePoint, StaysOpen = false, AllowsTransparency = true,
            PlacementRectangle = new Rect(point, new Size(0, 0)),
            Child = new Border
            {
                Width = 286, Padding = new Thickness(6), CornerRadius = new CornerRadius(9),
                Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Accent"),
                BorderThickness = new Thickness(1), Child = content
            }
        };
        popup.Opened += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (_textInput?.Popup != popup)
                return;
            field.Focus();
            field.CaretIndex = field.Text.Length;
        }, DispatcherPriority.Input);
        popup.Closed += (_, _) =>
        {
            if (_textInput?.Popup == popup)
                CommitTextInput();
        };
        _textInput = new AnnotationTextDraft(popup, field, mark, index);
        InvalidateVisual();
        popup.IsOpen = true;
    }

    private void FinishTextInput(TextInputCompletion completion, bool returnFocus = false)
    {
        if (_textInput is not { } input)
            return;

        _textInput = null;
        input.Popup.IsOpen = false;
        InvalidateVisual();
        if (completion != TextInputCompletion.Cancel)
            ApplyTextInput(input, deleteEmpty: completion == TextInputCompletion.Confirm);
        if (returnFocus && IsVisible)
            Focus();
    }

    private void ApplyTextInput(AnnotationTextDraft input, bool deleteEmpty)
    {
        var text = input.Field.Text.Trim();
        if (text == input.Mark.Label || (text.Length == 0 && !deleteEmpty))
            return;

        var updated = text.Length == 0 ? null : input.Mark with { Label = text };
        if (input.Index < 0)
        {
            if (updated is not null)
                Add(updated);
        }
        else if (input.Index < _marks.Count && ReferenceEquals(_marks[input.Index], input.Mark))
            Commit(new Edit(input.Index, input.Mark, updated));
    }

    private static double AnnotationTextSize(Annotation mark) => Math.Max(16, mark.Size * 5);

    private static void DrawAnnotationText(DrawingContext drawing, Annotation mark, Point start)
    {
        var size = AnnotationTextSize(mark);
        var color = mark.Brush.Color;
        var lightness = .2126 * color.R + .7152 * color.G + .0722 * color.B;
        var shadow = lightness > 140 ? DarkTextShadow : LightTextShadow;
        var label = new FormattedText(mark.Label!, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            AnnotationTypeface, size, shadow, 1) { LineHeight = size * 1.4 };
        drawing.DrawText(label, start + new Vector(1, 1));
        label.SetForegroundBrush(mark.Brush);
        drawing.DrawText(label, start);
    }

    private enum TextInputCompletion { Cancel, Dismiss, Confirm }
    private sealed record AnnotationTextDraft(Popup Popup, TextBox Field, Annotation Mark, int Index);
}
