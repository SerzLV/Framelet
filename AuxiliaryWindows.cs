using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Framelet;

internal static class AuxiliaryWindows
{
    internal static async Task ShowCaptureCountdownAsync()
    {
        const int seconds = 3;
        var accent = (Brush)Application.Current.FindResource("Accent");
        var label = new TextBlock
        {
            Text = UiText.Get("CaptureCountdown"),
            FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        var digit = new TextBlock
        {
            Text = seconds.ToString(), FontSize = 23, FontWeight = FontWeights.Bold, Foreground = accent,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var counter = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(9),
            Background = (Brush)Application.Current.FindResource("Canvas"), Child = digit, Margin = new Thickness(0, 0, 11, 0)
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(counter);
        content.Children.Add(label);
        var window = new Window
        {
            Title = UiText.Get("CaptureCountdown"), Width = 226, Height = 64, WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Content = new Border
            {
                Background = (Brush)Application.Current.FindResource("Panel"),
                BorderBrush = (Brush)Application.Current.FindResource("Stroke"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 0, 12, 0), Child = content
            }
        };
        var area = Forms.Screen.FromPoint(CaptureService.CursorPosition).WorkingArea;
        window.Left = area.Left;
        window.Top = area.Top;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            const uint keepSizeAndActivation = 0x0001 | 0x0004 | 0x0010;
            Native.SetWindowPos(handle, IntPtr.Zero, area.Left, area.Top, 0, 0, keepSizeAndActivation);
            if (Native.GetWindowRect(handle, out var bounds))
            {
                var gap = Math.Max(16, (int)Math.Round(16 * Native.GetDpiForWindow(handle) / 96d));
                var left = Math.Max(area.Left, area.Right - (bounds.Right - bounds.Left) - gap);
                Native.SetWindowPos(handle, IntPtr.Zero, left, area.Top + gap, 0, 0, keepSizeAndActivation);
            }
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr _, int message, IntPtr _, IntPtr _, ref bool handled) =>
            {
                const int mouseActivate = 0x0021, noActivateAndEat = 4;
                handled = message == mouseActivate;
                return handled ? new IntPtr(noActivateAndEat) : IntPtr.Zero;
            });
        };

        window.Show();
        try
        {
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                digit.Text = remaining.ToString();
                await Task.Delay(1000);
            }
        }
        finally
        {
            window.Close();
        }
    }

    internal static string? AskForText(Window owner, string? currentText = null)
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
            Text = currentText ?? "",
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
            Content = UiText.Get(currentText is null ? "Add" : "Save"),
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


