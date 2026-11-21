using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using ShapePath = System.Windows.Shapes.Path;

namespace Framelet;

internal enum SelectionOutput { Default, Copy, Save, Pin, Editor }
internal readonly record struct SelectionSwatch(string Tag, string Label, bool Recent = false);

internal sealed class SelectionToolbar : Window
{
    private readonly Dictionary<EditorTool, Button> _tools = [];
    private readonly WrapPanel _swatches = new();
    private readonly WrapPanel _recent = new();
    private readonly StackPanel _appearance = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 5, 0, 1) };
    private readonly Slider _stroke;
    private readonly ToggleButton _heads;
    private readonly Ellipse _inkPreview;
    private readonly TextBlock _strokeValue;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _delete;
    private readonly Button _frame;
    private readonly TextBlock _status = new() { Visibility = Visibility.Collapsed, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private bool _syncing;
    private Drawing.Rectangle _selection;

    internal event Action<EditorTool>? ToolRequested;
    internal event Action<string>? ColorRequested;
    internal event Action<double>? StrokeChanged;
    internal event Action? StrokeStarted;
    internal event Action? StrokeCompleted;
    internal event Action<bool>? HeadsChanged;
    internal event Action? UndoRequested;
    internal event Action? RedoRequested;
    internal event Action? DeleteRequested;
    internal event Action? FrameRequested;
    internal event Action<bool>? OcrRequested;
    internal event Action<SelectionOutput>? OutputRequested;
    internal event Action? CancelRequested;

    internal SelectionToolbar()
    {
        Title = UiText.Get("SelectionToolbar");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Foreground = Brush("Text");
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        var tools = new WrapPanel();
        AddTool(EditorTool.Select, "PointerIcon", "SelectTool");
        AddTool(EditorTool.Arrow, "ArrowIcon", "ArrowTool");
        AddTool(EditorTool.CurveArrow, "CurveIcon", "CurveTool");
        AddTool(EditorTool.Line, "LineIcon", "LineTool");
        AddTool(EditorTool.Rectangle, "RectIcon", "RectangleTool");
        AddTool(EditorTool.Ellipse, "EllipseIcon", "EllipseTool");
        AddTool(EditorTool.Pen, "PenIcon", "PenTool");
        AddTool(EditorTool.Marker, "MarkerIcon", "MarkerTool");
        AddTool(EditorTool.Text, "TextIcon", "TextTool");
        AddTool(EditorTool.Number, "NumberIcon", "NumberTool");
        AddTool(EditorTool.Redact, "RedactIcon", "RedactTool");
        var color = AddButton(tools, "Color", null, () =>
        {
            _appearance.Visibility = _appearance.IsVisible ? Visibility.Collapsed : Visibility.Visible;
            Reposition();
        });
        var colorLabel = new StackPanel { Orientation = Orientation.Horizontal };
        _inkPreview = new Ellipse { Width = 10, Height = 10, Stroke = Brush("Muted"), StrokeThickness = .7,
            Margin = new Thickness(0, 0, 5, 0) };
        colorLabel.Children.Add(_inkPreview);
        colorLabel.Children.Add(new TextBlock { Text = $"{UiText.Get("Color")} ▾" });
        color.Content = colorLabel;
        _undo = AddButton(tools, "UndoTip", "UndoIcon", () => UndoRequested?.Invoke());
        _redo = AddButton(tools, "RedoTip", "RedoIcon", () => RedoRequested?.Invoke());
        _delete = AddButton(tools, "Delete", "TrashIcon", () => DeleteRequested?.Invoke());

        _appearance.Children.Add(_swatches);
        _appearance.Children.Add(_recent);
        var options = new WrapPanel { Margin = new Thickness(1, 3, 2, 0) };
        options.Children.Add(new TextBlock { Text = UiText.Get("Thickness"), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        _stroke = new Slider
        {
            Minimum = 1, Maximum = 12, Width = 88, Value = 4, TickFrequency = 1, IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true, Margin = new Thickness(8, 0, 4, 0),
            Style = (Style)FindResource("StrokeSlider")
        };
        AutomationProperties.SetName(_stroke, UiText.Get("Thickness"));
        _strokeValue = new TextBlock { Text = "4", FontSize = 11, MinWidth = 17, Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brush("Muted"), VerticalAlignment = VerticalAlignment.Center };
        _stroke.ValueChanged += (_, e) =>
        {
            _strokeValue.Text = $"{e.NewValue:0}";
            if (!_syncing)
                StrokeChanged?.Invoke(e.NewValue);
        };
        _stroke.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => StrokeStarted?.Invoke()));
        _stroke.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => StrokeCompleted?.Invoke()));
        options.Children.Add(_stroke);
        options.Children.Add(_strokeValue);
        _heads = new ToggleButton { Style = (Style)FindResource("OptionToggle"), Content = UiText.Get("SelectionBothHeads"),
            VerticalAlignment = VerticalAlignment.Center };
        _heads.Click += (_, _) => HeadsChanged?.Invoke(_heads.IsChecked == true);
        options.Children.Add(_heads);
        _appearance.Children.Add(options);

        var actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _frame = AddButton(actions, "SelectionAdjustFrame", "CropIcon", () => FrameRequested?.Invoke());
        _frame.Visibility = Visibility.Collapsed;
        var redact = AddButton(actions, "OcrRedactContacts", "RedactIcon", () => OcrRequested?.Invoke(true));
        redact.ToolTip = $"{UiText.Get("OcrRedactContacts")}\n{UiText.Get("OcrRedactSourceNote")}";
        AddButton(actions, "SelectionCopyText", "OcrIcon", () => OcrRequested?.Invoke(false));
        AddButton(actions, "Pin", "PinIcon", () => OutputRequested?.Invoke(SelectionOutput.Pin));
        AddButton(actions, "SaveTooltip", "SaveIcon", () => OutputRequested?.Invoke(SelectionOutput.Save));
        AddButton(actions, "SelectionOpenEditor", "LaunchIcon", () => OutputRequested?.Invoke(SelectionOutput.Editor));
        AddButton(actions, "Copy", null, () => OutputRequested?.Invoke(SelectionOutput.Copy), primary: true);
        AddButton(actions, "SelectionClose", "CloseIcon", () => CancelRequested?.Invoke());
        _status.Foreground = Brush("Muted");
        _status.Margin = new Thickness(4, 5, 4, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var content = new StackPanel();
        content.Children.Add(tools);
        content.Children.Add(_appearance);
        content.Children.Add(actions);
        content.Children.Add(_status);
        Content = new Border
        {
            Background = Brush("Panel"), BorderBrush = Brush("Stroke"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(5),
            Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(Reposition, DispatcherPriority.Loaded);
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(Reposition, DispatcherPriority.Loaded);
        _statusTimer.Tick += (_, _) => SetStatus(null);
        Closed += (_, _) => _statusTimer.Stop();

        void AddTool(EditorTool tool, string icon, string label) =>
            _tools.Add(tool, AddButton(tools, label, icon, () => ToolRequested?.Invoke(tool)));
    }

    internal void SetColors(IEnumerable<SelectionSwatch> colors)
    {
        _swatches.Children.Clear();
        _recent.Children.Clear();
        var palette = colors.ToArray();
        if (palette.Any(color => color.Recent))
            _recent.Children.Add(new TextBlock
            {
                Text = UiText.Get("RecentColors"), Foreground = Brush("Muted"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0)
            });
        foreach (var (tag, label, recent) in palette)
        {
            var swatch = new Ellipse { Width = 13, Height = 13, Stroke = Brush("Muted"), StrokeThickness = .7,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tag)) };
            var button = new Button
            {
                Style = (Style)FindResource("IconButton"), Width = 27, Height = 27, Padding = new Thickness(3),
                Content = swatch, ToolTip = label, Margin = new Thickness(0, 0, 1, 0)
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => ColorRequested?.Invoke(tag);
            (recent ? _recent : _swatches).Children.Add(button);
        }
        Reposition();
    }

    internal void UpdateEditor(EditorSurface? editor, bool editing, Color ink, double stroke, bool heads)
    {
        _syncing = true;
        try
        {
            foreach (var (tool, button) in _tools)
            {
                var selected = editing && editor?.Tool == tool;
                button.BorderBrush = selected ? Brush("Accent") : Brushes.Transparent;
                button.Foreground = selected ? Brush("Accent") : Brush("Text");
            }
            _delete.IsEnabled = editing && editor?.HasSelection == true;
            _frame.Visibility = editing && editor?.HasAnnotations == false ? Visibility.Visible : Visibility.Collapsed;
            _undo.IsEnabled = editor?.CanUndo == true;
            _redo.IsEnabled = editor?.CanRedo == true;
            _stroke.IsEnabled = editor?.CanChangeAppearance != false;
            _inkPreview.Fill = new SolidColorBrush(editor?.ActiveInk ?? ink);
            _stroke.Value = editor?.ActiveStrokeWidth ?? stroke;
            _heads.IsChecked = editor?.CurveHasBothHeads ?? heads;
        }
        finally { _syncing = false; }
    }

    internal void SetStatus(string? message, bool temporary = true)
    {
        _statusTimer.Stop();
        _status.Text = message;
        _status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        if (temporary && _status.Visibility == Visibility.Visible)
            _statusTimer.Start();
        Reposition();
    }

    internal void ShowAt(Drawing.Rectangle selection)
    {
        _selection = selection;
        if (!IsVisible)
        {
            var point = Corner(selection);
            var handle = new WindowInteropHelper(this).EnsureHandle();
            Native.SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
            Show();
        }
        Reposition();
    }

    internal void Reposition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!IsVisible || handle == IntPtr.Zero)
            return;
        var area = Forms.Screen.FromPoint(Corner(_selection)).WorkingArea;
        var dpi = Native.GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1 : dpi / 96d;
        var maxWidth = Math.Max(1, area.Width / scale);
        var maxHeight = Math.Max(1, area.Height / scale);
        if (MaxWidth != maxWidth)
            MaxWidth = maxWidth;
        if (MaxHeight != maxHeight)
            MaxHeight = maxHeight;
        UpdateLayout();
        if (!Native.GetWindowRect(handle, out var bounds))
            return;
        var position = PlaceToolbar(_selection, new Drawing.Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
            area, (int)Math.Ceiling(8 * scale));
        Native.SetWindowPos(handle, new IntPtr(-1), position.X, position.Y, 0, 0, 0x0001 | 0x0010);
    }

    internal static Drawing.Point PlaceToolbar(Drawing.Rectangle selection, Drawing.Size size, Drawing.Rectangle workArea, int gap = 8)
    {
        var below = selection.Bottom + gap;
        var above = selection.Top - gap - size.Height;
        var y = below + size.Height <= workArea.Bottom ? below
            : above >= workArea.Top ? above : selection.Bottom - gap - size.Height;
        return new Drawing.Point(
            Math.Clamp(selection.Right - size.Width, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
    }

    private static Drawing.Point Corner(Drawing.Rectangle selection) => new(selection.Right - 1, selection.Bottom - 1);
    private Brush Brush(string key) => (Brush)FindResource(key);

    private Button AddButton(Panel parent, string label, string? icon, Action action, bool primary = false)
    {
        var text = UiText.Get(label);
        var button = new Button
        {
            Style = (Style)FindResource(primary ? "PrimaryButton" : icon is null ? "GhostButton" : "EditorToolButton"),
            Height = 31, Margin = new Thickness(0, 0, 1, 1),
            Padding = icon is null ? new Thickness(9, 3, 9, 3) : new Thickness(6),
            Content = icon is null ? text : new ShapePath
            {
                Style = (Style)FindResource("Icon"), Data = (Geometry)FindResource(icon)
            },
            ToolTip = text
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        parent.Children.Add(button);
        return button;
    }
}
