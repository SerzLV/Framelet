using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Framelet;

internal static class AuxiliaryWindows
{
    internal static string? AskForText(Window owner)
    {
        var window = new Window
        {
            Title = UiText.Get("AnnotationText"),
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Width = 428,
            SizeToContent = SizeToContent.Height
        };
        var layout = new StackPanel { Margin = new Thickness(18) };
        var field = new TextBox
        {
            MinHeight = 87,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = 450,
            Margin = new Thickness(0, 0, 0, 13)
        };
        System.Windows.Automation.AutomationProperties.SetName(field, UiText.Get("AnnotationText"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = UiText.Get("Cancel"), IsCancel = true, Margin = new Thickness(0, 0, 7, 0) };
        var apply = new Button
        {
            Content = UiText.Get("Add"),
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            ToolTip = "Ctrl + Enter"
        };
        apply.Click += (_, _) => window.DialogResult = true;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                window.DialogResult = true;
            }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        layout.Children.Add(field);
        layout.Children.Add(buttons);
        window.Content = layout;
        window.Loaded += (_, _) => field.Focus();
        return window.ShowDialog() == true ? field.Text : null;
    }

    internal static Window ShowPin(Window owner, BitmapSource image)
    {
        var ratio = Math.Min(1, Math.Min(570d / image.PixelWidth, 360d / image.PixelHeight));
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = Math.Max(112, image.PixelWidth * ratio + 2),
            Height = image.PixelHeight * ratio + 31,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.SetResourceReference(Window.TitleProperty, UiText.Key("PinnedTitle"));
        var position = owner.PointToScreen(new Point(Math.Max(0, owner.ActualWidth - window.Width - 65), 102));
        var fromDevice = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var anchor = fromDevice.Transform(position);
        var workArea = Forms.Screen.FromPoint(new Drawing.Point((int)position.X, (int)position.Y)).WorkingArea;
        var area = new Rect(fromDevice.Transform(new Point(workArea.Left, workArea.Top)),
            fromDevice.Transform(new Point(workArea.Right, workArea.Bottom)));
        window.Left = Math.Clamp(anchor.X, area.Left, Math.Max(area.Left, area.Right - window.Width));
        window.Top = Math.Clamp(anchor.Y, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
        var frame = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("Stroke"),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.FindResource("Panel")
        };
        var layout = new DockPanel();
        var bar = new DockPanel { Height = 29, Background = Brushes.Transparent };
        var close = new Button
        {
            Content = "×",
            Padding = new Thickness(7, 0, 7, 2),
            Width = 30,
            Style = (Style)Application.Current.FindResource("GhostButton")
        };
        close.SetResourceReference(FrameworkElement.ToolTipProperty, UiText.Key("ClosePin"));
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(new TextBlock
        {
            Text = $"{image.PixelWidth} × {image.PixelHeight}",
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Add(bar);
        var preview = new Image { Source = image, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        layout.Children.Add(preview);
        frame.Child = layout;
        window.Content = frame;
        bar.MouseLeftButtonDown += Drag;
        preview.MouseLeftButtonDown += Drag;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                window.Close();
        };
        window.Show();
        return window;

        void Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                window.Close();
                return;
            }

            window.DragMove();
        }
    }
}


