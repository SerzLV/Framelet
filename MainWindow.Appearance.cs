using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Framelet;

public partial class MainWindow
{
    private const int RecentColorLimit = 4;
    private readonly DispatcherTimer _appearanceSaveTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(600)
    };
    private bool _workspaceWasShown;
    private bool _workspaceWasMaximized;
    private PalettePreferences? _palette;

    private void InitializeAppearance()
    {
        _appearanceSaveTimer.Tick += (_, _) => SaveAppearance();
        SizeChanged += (_, _) => ScheduleAppearanceSave();

        _palette = _preferences.Palette;
        if (_palette is { } palette)
        {
            var ink = PaletteSwatch(palette.Color)?.Tag is string color
                ? (Color)ColorConverter.ConvertFromString(color) : EditorImage.Ink;
            var width = double.IsFinite(palette.StrokeWidth)
                ? Math.Clamp(palette.StrokeWidth, StrokeSize.Minimum, StrokeSize.Maximum) : EditorImage.StrokeWidth;
            EditorImage.SetDrawingDefaults(ink, width);
        }
        UpdateSelectionOptions();

        if (_preferences.WindowSize is { IsValid: true } saved)
        {
            Width = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
            Height = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
            _workspaceWasMaximized = saved.Maximized;
        }
    }

    private void RestoreWorkspaceSize()
    {
        if (_preferences.WindowSize is not { IsValid: true } saved)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = Native.GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1 : dpi / 96d;
        Width = Math.Clamp(saved.Width, MinWidth, Math.Max(MinWidth, area.Width / scale));
        Height = Math.Clamp(saved.Height, MinHeight, Math.Max(MinHeight, area.Height / scale));
        if (saved.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void ObserveWorkspaceState()
    {
        if (!_workspaceWasShown || WindowState == WindowState.Minimized)
            return;

        _workspaceWasMaximized = WindowState == WindowState.Maximized;
        ScheduleAppearanceSave();
    }

    private void ScheduleAppearanceSave()
    {
        if (!_workspaceWasShown && _palette == _preferences.Palette)
            return;

        _appearanceSaveTimer.Stop();
        _appearanceSaveTimer.Start();
    }

    private void SaveAppearance()
    {
        _appearanceSaveTimer.Stop();
        var saved = _preferences.WindowSize;
        if (_workspaceWasShown)
        {
            var size = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : RestoreBounds.Size;
            var latest = new WorkspaceSize(Math.Round(size.Width, 2), Math.Round(size.Height, 2), _workspaceWasMaximized);
            if (latest.IsValid)
                saved = latest;
        }
        if (saved != _preferences.WindowSize || _palette != _preferences.Palette)
            TrySavePreferences(_preferences with { WindowSize = saved, Palette = _palette });
    }

    private Button? PaletteSwatch(string? color) => ColorButtons.Children.OfType<Button>()
        .FirstOrDefault(button => string.Equals((string)button.Tag, color, StringComparison.OrdinalIgnoreCase));

    private string[] RecentColorTags(string active) => (_palette?.RecentColors ?? []).Prepend(active)
        .Select(tag => PaletteSwatch(tag)?.Tag as string).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentColorLimit).ToArray();

    private PalettePreferences DrawingPalette => _palette
        ?? new PalettePreferences(ColorTag(EditorImage.Ink), [], EditorImage.StrokeWidth);

    private static string ColorTag(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void RememberColor(string color)
    {
        var ink = (Color)ColorConverter.ConvertFromString(color);
        if (EditorImage.Ink != ink)
        {
            EditorImage.SetDrawingDefaults(ink, EditorImage.StrokeWidth);
            UpdateSelectionOptions();
        }

        var recent = RecentColorTags(color);
        if (_palette?.Color == color && (_palette.RecentColors ?? []).SequenceEqual(recent))
            return;

        _palette = DrawingPalette with { Color = color, RecentColors = recent };
        ScheduleAppearanceSave();
    }

    private void RememberStrokeWidth(double width)
    {
        if (EditorImage.StrokeWidth != width)
        {
            EditorImage.SetDrawingDefaults(EditorImage.Ink, width);
            UpdateSelectionOptions();
        }
        if (_palette?.StrokeWidth == width)
            return;

        _palette = DrawingPalette with { StrokeWidth = width };
        ScheduleAppearanceSave();
    }

    private void RecentColors_Click(object sender, RoutedEventArgs e)
    {
        if (RecentColorsButton.FindResource("RecentColorsMenu") is not ContextMenu menu)
            return;

        menu.Items.Clear();
        var active = ColorTag(EditorImage.Ink);
        foreach (var color in RecentColorTags(active))
        {
            var item = new MenuItem
            {
                Header = PaletteSwatch(color)?.ToolTip ?? color,
                Tag = color,
                Style = (Style)FindResource("HistoryMenuItem"),
                Icon = new Ellipse
                {
                    Width = 13, Height = 13, Stroke = (Brush)FindResource("Muted"), StrokeThickness = .7,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
                }
            };
            item.Click += (_, _) => SelectColor(color);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = RecentColorsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr window);
}
