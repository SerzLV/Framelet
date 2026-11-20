using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Framelet;

public partial class MainWindow : Window
{
    private readonly HistoryStore _history = new();
    private readonly DispatcherTimer _editSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private readonly List<Window> _pins = [];
    private IReadOnlyList<HistoryItem> _recent = [];
    private Task? _initialHistoryLoad;
    private Preferences _preferences;
    private string? _entryPath;
    private int _revision;
    private int _savedRevision;
    private bool _busy;
    private bool _quitting;
    private bool _fit = true;
    private bool _syncingEditorOptions;
    private double _zoom = 1;
    private Point? _panStart;
    private Vector _panOffsets;

    public MainWindow()
    {
        _preferences = _history.LoadPreferences();
        _history.ConfigureStorage(_preferences);
        _foregroundTracker = new ForegroundWindowTracker(((App)Application.Current).InitialForeground);
        _printScreen = new PrintScreenShortcut(Dispatcher, RequestQuickCapture);
        UiText.Apply(_preferences.Language);
        InitializeComponent();
        HistoryItems.ItemsSource = _historyCards;
        InitializeCaptureShortcuts();
        Language = XmlLanguage.GetLanguage(UiText.Culture.Name);
        EditorImage.RequestText = text => AuxiliaryWindows.AskForText(this, text);
        EditorImage.Changed += EditorChanged;
        EditorImage.SelectionChanged += UpdateSelectionOptions;
        CropLayer.SelectionChanged += UpdateCropSelection;
        OcrCanvasText.SelectionChanged += UpdateOcrSelectionAction;
        StrokeSize.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => EditorImage.StartStrokeAdjustment()));
        StrokeSize.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EditorImage.CompleteStrokeAdjustment()));
        _editSaveTimer.Tick += EditSaveTimer_Tick;
        SelectTool(EditorTool.Arrow);
        InitializeAppearance();
        EditorChanged();

        SourceInitialized += (_, _) =>
        {
            RestoreWorkspaceSize();
            AttachCaptureCommands();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _workspaceWasShown = true;
                _ = EnsureHistoryLoadedAsync();
            }
            else
                SaveAppearance();
        };
    }

    private Task EnsureHistoryLoadedAsync() => _initialHistoryLoad ??= LoadInitialHistoryAsync();

    private async Task LoadInitialHistoryAsync()
    {
        try
        {
            RefreshHistory(await Task.Run(_history.Load));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(ex);
            Report(UiText.Get("FileFailure"), true);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveAppearance();
        if (!_quitting && _tray is not null)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelOcrRequest();
        _appearanceSaveTimer.Stop();
        _editSaveTimer.Stop();
        DetachCaptureCommands();
        foreach (var pin in _pins.ToArray())
            pin.Close();
        base.OnClosed(e);
    }

    private void OpenEditor(BitmapSource image, string? path, EditJournal? journal = null)
    {
        EndCrop(returnFocus: false);
        CloseOcr();
        _entryPath = path;
        HomeView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        EditorImage.Reset(image, journal);
        _savedRevision = path is null ? -1 : _revision;
        _fit = true;
        UpdateLayout();
        ApplyFit();
        EditorScroll.ScrollToHorizontalOffset(0);
        EditorScroll.ScrollToVerticalOffset(0);
        EditorImage.Focus();
    }

    private async Task OpenCapturedImageAsync(BitmapSource image, string status)
    {
        OpenEditor(image, null);
        var revision = _revision;
        ShowWorkspace();
        Report(UiText.Get(status));
        await EnsureHistoryLoadedAsync();
        await RememberEditorAsync(image, revision);
    }

    private async void Home_Click(object sender, RoutedEventArgs e) => await ReturnHomeAsync();

    private Task ReturnHomeAsync()
    {
        return RunOperation(async () =>
        {
            if (!await SaveBeforeLeavingAsync())
                return;
            CloseOcr();
            EditorView.Visibility = Visibility.Collapsed;
            EditorImage.Clear();
            HomeView.Visibility = Visibility.Visible;
            Report(UiText.Get("Ready"));
        });
    }

    private async Task<bool> SaveBeforeLeavingAsync()
    {
        EndCrop(returnFocus: false);
        var wasEnabled = EditorView.IsEnabled;
        EditorImage.CancelDraft();
        EditorView.IsEnabled = false;
        try
        {
            return await RememberEditorAsync();
        }
        finally
        {
            EditorView.IsEnabled = wasEnabled;
        }
    }

    private async Task<bool> RememberEditorAsync(BitmapSource? prepared = null, int? preparedRevision = null)
    {
        EditorImage.CompleteStrokeAdjustment();
        _editSaveTimer.Stop();
        var revision = _revision;
        if (!_preferences.KeepHistory || EditorView.Visibility != Visibility.Visible || _savedRevision == revision)
            return true;

        var path = _entryPath ?? _history.CreateEntryPath();
        try
        {
            var document = EditorImage.CaptureJournal();
            var image = prepared is not null && preparedRevision == revision ? prepared : await EditorImage.ExportAsync();
            var item = await Task.Run(() => _history.Put(path, image, document));
            _entryPath = path;
            _savedRevision = revision;
            if (_lastCaptureImage is { } latest && EditorImage.IsShowingSource(latest))
                LinkCaptureToHistory(latest, path);
            AddRecent(item);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            Report(UiText.Get("HistoryWriteFailure"), true);
            return false;
        }
    }

    private void AddRecent(HistoryItem item)
    {
        RefreshHistory(_recent.Where(old => old.FilePath != item.FilePath).Append(item)
            .OrderByDescending(entry => entry.CreatedUtc).ThenByDescending(entry => entry.FilePath, StringComparer.Ordinal)
            .ToArray());
    }

    private async void EditSaveTimer_Tick(object? sender, EventArgs e)
    {
        if (!_busy)
            await RunOperation(async () => await RememberEditorAsync());
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();

    private Task CopyAsync()
    {
        return RunOperation(async () =>
        {
            EditorImage.CompleteStrokeAdjustment();
            var revision = _revision;
            var image = await EditorImage.ExportAsync();
            await CopyImageToClipboardAsync(image);
            Report(UiText.Get("ImageCopied"));
            await RememberEditorAsync(image, revision);
        });
    }

    private static async Task CopyImageToClipboardAsync(BitmapSource image)
    {
        using var png = new MemoryStream();
        await Task.Run(() => HistoryStore.EncodePng(png, image));
        png.Position = 0;
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", png, false);
        await AccessClipboardAsync(() =>
        {
            png.Position = 0;
            Clipboard.SetDataObject(data, true);
        });
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private Task SaveAsync()
    {
        return RunOperation(async () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = UiText.Get("PngFilter"),
                DefaultExt = ".png",
                AddExtension = true,
                FileName = _recent.FirstOrDefault(entry => entry.FilePath == _entryPath)?.DisplayName
                    ?? HistoryStore.DefaultFileName(DateTime.Now)
            };
            if (dialog.ShowDialog(this) != true)
                return;

            EditorImage.CompleteStrokeAdjustment();
            var revision = _revision;
            var image = await EditorImage.ExportAsync();
            await Task.Run(() => HistoryStore.WritePng(dialog.FileName, image));
            Report(UiText.Format("SavedFile", Path.GetFileName(dialog.FileName)));
            await RememberEditorAsync(image, revision);
        });
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        await RunOperation(async () =>
        {
            EditorImage.CompleteStrokeAdjustment();
            var revision = _revision;
            var image = await EditorImage.ExportAsync();
            var pin = AuxiliaryWindows.ShowPin(this, image);
            _pins.Add(pin);
            pin.Closed += (_, _) => _pins.Remove(pin);
            Report(UiText.Get("Pinned"));
            await RememberEditorAsync(image, revision);
        });
    }

    private void EditorChanged()
    {
        _revision++;
        UndoButton.IsEnabled = EditorImage.CanUndo;
        RedoButton.IsEnabled = EditorImage.CanRedo;
        if (!double.IsNaN(EditorImage.Width))
        {
            var dimensions = $"{EditorImage.Width:0} × {EditorImage.Height:0} px";
            if (ImageSize.Text != dimensions)
            {
                ImageSize.Text = dimensions;
                if (_fit)
                    ApplyFit();
            }
        }
        InvalidateOcr();
        if (_preferences.KeepHistory && EditorView.Visibility == Visibility.Visible)
        {
            _editSaveTimer.Stop();
            _editSaveTimer.Start();
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<EditorTool>(tag, out var tool))
            SelectTool(tool);
    }

    private void SelectTool(EditorTool tool)
    {
        LeaveOcrImageMode();
        StopPanning();
        EditorImage.Tool = tool;
        if (tool == EditorTool.Hand)
            EditorCanvasContent.Cursor = Cursors.Hand;
        else
            EditorCanvasContent.ClearValue(CursorProperty);
        UpdateEditorToolState();
    }

    private void UpdateEditorToolState()
    {
        var textMode = OcrCanvasText.IsActive;
        foreach (var button in new[] { NavigationTools, ShapeTools, MarkupTools }.SelectMany(group => group.Children.OfType<Button>()))
        {
            var selected = !textMode && string.Equals((string)button.Tag, EditorImage.Tool.ToString(), StringComparison.Ordinal);
            SetSelected(button, selected, accentText: true);
            if (selected)
                ActiveToolText.SetBinding(TextBlock.TextProperty, new Binding
                {
                    Source = button,
                    Path = new PropertyPath("(0)", System.Windows.Automation.AutomationProperties.NameProperty)
                });
        }
        if (textMode)
            ActiveToolText.SetResourceReference(TextBlock.TextProperty, UiText.Key("OcrTitle"));

        UpdateEditorHint();
        UpdateSelectionOptions();
    }

    private void UpdateEditorHint()
    {
        ToolHint.SetResourceReference(TextBlock.TextProperty, UiText.Key(OcrCanvasText.IsActive
            ? OcrCanvasText.HasWords ? "OcrImageHint" : "OcrWorking" : EditorImage.Tool switch
        {
            EditorTool.Select => "SelectHint",
            EditorTool.Hand => "HandHint",
            EditorTool.Arrow => "ArrowHint",
            EditorTool.Line => "LineHint",
            EditorTool.CurveArrow => "CurveHint",
            EditorTool.Rectangle => "RectangleHint",
            EditorTool.Ellipse => "EllipseHint",
            EditorTool.Pen => "PenHint",
            EditorTool.Marker => "MarkerHint",
            EditorTool.Text => "TextHint",
            EditorTool.Number => "NumberHint",
            _ => "RedactHint"
        }));
    }

    private void UpdateSelectionOptions()
    {
        DeleteAnnotationButton.IsEnabled = EditorImage.HasSelection;
        var showStyleOptions = !OcrCanvasText.IsActive;
        CurveHeads.Visibility = showStyleOptions && EditorImage.ShowCurveOptions ? Visibility.Visible : Visibility.Collapsed;
        CurveHeads.IsChecked = EditorImage.CurveHasBothHeads;
        var ink = EditorImage.ActiveInk;
        foreach (var button in ColorButtons.Children.OfType<Button>())
            SetSelected(button, (Color)ColorConverter.ConvertFromString((string)button.Tag) == ink, accentText: false);

        _syncingEditorOptions = true;
        try
        {
            RecentColorsButton.IsEnabled = ColorButtons.IsEnabled = StrokeSize.IsEnabled = showStyleOptions && EditorImage.CanChangeAppearance;
            var caption = UiText.Key(EditorImage.ActiveSizeIsTextSize ? "Size" : "Thickness");
            StrokeCaption.SetResourceReference(TextBlock.TextProperty, caption);
            StrokeSize.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, caption);
            StrokeSize.Value = EditorImage.ActiveStrokeWidth;
            StrokeValue.Text = $"{StrokeSize.Value:0}";
        }
        finally
        {
            _syncingEditorOptions = false;
        }
    }

    private void CurveHeads_Click(object sender, RoutedEventArgs e) => EditorImage.SetCurveHeads(CurveHeads.IsChecked == true);

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
            SelectColor(color);
    }

    private void SelectColor(string color)
    {
        EditorImage.SetInk((Color)ColorConverter.ConvertFromString(color));
        RememberColor(color);
        UpdateSelectionOptions();
    }

    private void SetSelected(Button button, bool selected, bool accentText)
    {
        button.Background = selected ? (Brush)FindResource("PanelHover") : Brushes.Transparent;
        button.BorderBrush = selected ? (Brush)FindResource("Accent") : Brushes.Transparent;
        if (accentText)
            button.Foreground = (Brush)FindResource(selected ? "Accent" : "Text");
    }

    private void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingEditorOptions || EditorImage is null || StrokeValue is null)
            return;

        EditorImage.SetStrokeWidth(e.NewValue);
        StrokeValue.Text = $"{e.NewValue:0}";
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => EditorImage.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => EditorImage.Redo();
    private void DeleteAnnotation_Click(object sender, RoutedEventArgs e) => EditorImage.DeleteSelection();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(.8);
    private void ZoomActual_Click(object sender, RoutedEventArgs e) => SetManualZoom(1);
    private void Fit_Click(object sender, RoutedEventArgs e) { _fit = true; ApplyFit(); }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fit && EditorView.Visibility == Visibility.Visible)
            ApplyFit();
    }

    private void ApplyFit()
    {
        if (double.IsNaN(EditorImage.Width) || EditorViewport.ActualWidth < 48 || EditorViewport.ActualHeight < 48)
            return;

        var zoom = Math.Min((EditorViewport.ActualWidth - 44) / EditorImage.Width,
            (EditorViewport.ActualHeight - 44) / EditorImage.Height);
        ApplyZoom(Math.Clamp(zoom, .005, 1));
    }

    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var factor = Math.Pow(1.18, e.Delta / (double)Mouse.MouseWheelDeltaForOneLine);
        SetManualZoom(_zoom * factor, e.GetPosition(EditorScroll));
    }

    private void Pan_Start(object sender, MouseButtonEventArgs e)
    {
        if (IsCropping || OcrCanvasText.IsActive || EditorImage.Tool != EditorTool.Hand || !EditorCanvasContent.CaptureMouse())
            return;

        EditorImage.Focus();
        _panStart = e.GetPosition(EditorScroll);
        _panOffsets = new Vector(EditorScroll.HorizontalOffset, EditorScroll.VerticalOffset);
        e.Handled = true;
    }

    private void Pan_Move(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start)
            return;

        var offset = _panOffsets + (start - e.GetPosition(EditorScroll));
        EditorScroll.ScrollToHorizontalOffset(offset.X);
        EditorScroll.ScrollToVerticalOffset(offset.Y);
        e.Handled = true;
    }

    private void Pan_End(object sender, MouseButtonEventArgs e) => e.Handled = StopPanning();

    private void Pan_LostCapture(object sender, MouseEventArgs e) => _panStart = null;

    private bool StopPanning()
    {
        if (_panStart is null)
            return false;

        _panStart = null;
        if (EditorCanvasContent.IsMouseCaptured)
            EditorCanvasContent.ReleaseMouseCapture();
        return true;
    }

    private void ZoomBy(double factor) => SetManualZoom(_zoom * factor);

    private void SetManualZoom(double zoom, Point? pointer = null)
    {
        _fit = false;
        zoom = Math.Clamp(zoom, .005, 6);
        if (zoom == _zoom)
            return;

        var anchor = pointer ?? new Point(EditorScroll.ViewportWidth / 2, EditorScroll.ViewportHeight / 2);
        var pixel = EditorScroll.TranslatePoint(anchor, EditorImage);
        ApplyZoom(zoom);
        EditorScroll.UpdateLayout();
        var shift = EditorImage.TranslatePoint(pixel, EditorScroll) - anchor;
        EditorScroll.ScrollToHorizontalOffset(EditorScroll.HorizontalOffset + shift.X);
        EditorScroll.ScrollToVerticalOffset(EditorScroll.VerticalOffset + shift.Y);
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = zoom;
        EditorPage.LayoutTransform = new ScaleTransform(zoom, zoom);
        EditorImage.SetZoom(zoom);
        OcrCanvasText.SetZoom(zoom);
        CropLayer.SetZoom(zoom);
        ZoomLabel.Text = $"{Math.Max(1, Math.Round(zoom * 100))}%";
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (RenameLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                if (!_busy)
                    CloseRename();
                e.Handled = true;
            }
            return;
        }

        if (SettingsLayer.Visibility == Visibility.Visible)
        {
            if (_recordingCaptureShortcut is not null)
            {
                RecordCaptureShortcut(e);
                return;
            }
            if (_recordingWorkspaceShortcut)
            {
                RecordWorkspaceShortcut(e);
                return;
            }
            if (e.Key == Key.Escape)
            {
                CloseSettings();
                e.Handled = true;
            }
            return;
        }
        if (IsCropping && HandleCropKey(e))
            return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V
            && Keyboard.FocusedElement is not TextBox { IsReadOnly: false })
        {
            e.Handled = true;
            if (!e.IsRepeat)
                await PasteImageAsync();
            return;
        }

        if (EditorView.Visibility != Visibility.Visible || !EditorView.IsEnabled)
            return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
        {
            e.Handled = true;
            if (!e.IsRepeat)
                await OpenOcrAsync();
            return;
        }
        if (OcrText.IsKeyboardFocusWithin && e.Key != Key.Escape)
            return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            switch (e.Key)
            {
                case Key.C when OcrCanvasText.IsActive && (OcrCanvasText.HasSelection || _ocrRequest is not null):
                    await CopyOcrShortcutAsync(); break;
                case Key.C: await CopyAsync(); break;
                case Key.A when OcrCanvasText.IsActive: OcrCanvasText.SelectAll(); break;
                case Key.S: await SaveAsync(); break;
                case Key.Z: EditorImage.Undo(); break;
                case Key.Y: EditorImage.Redo(); break;
                case Key.D0: _fit = true; ApplyFit(); break;
                case Key.D1: SetManualZoom(1); break;
                default: e.Handled = false; break;
            }
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (e.Key == Key.C)
        {
            e.Handled = true;
            if (!e.IsRepeat)
                BeginCrop();
            return;
        }

        if (e.Key == Key.Delete)
        {
            e.Handled = EditorImage.DeleteSelection();
            return;
        }

        if (e.Key == Key.F2 && !e.IsRepeat && EditorImage.EditSelectedText())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (EditorImage.IsMouseCaptured)
                EditorImage.CancelDraft();
            else if (StopPanning())
                return;
            else if (OcrCanvasText.IsActive && (OcrCanvasText.HasSelection || OcrCanvasText.HasPendingSelection))
            {
                _copyWhenOcrReady = false;
                OcrCanvasText.ClearSelection();
            }
            else if (!OcrPanel.IsKeyboardFocusWithin && EditorImage.DeselectAnnotation())
                return;
            else if (OcrPanel.Visibility == Visibility.Visible)
                CloseOcr();
            else
                await ReturnHomeAsync();
            return;
        }

        EditorTool? tool = e.Key switch
        {
            Key.A => EditorTool.Arrow,
            Key.S => EditorTool.Select,
            Key.H => EditorTool.Hand,
            Key.L => EditorTool.Line,
            Key.V => EditorTool.CurveArrow,
            Key.R => EditorTool.Rectangle,
            Key.O => EditorTool.Ellipse,
            Key.P => EditorTool.Pen,
            Key.M => EditorTool.Marker,
            Key.T => EditorTool.Text,
            Key.N => EditorTool.Number,
            Key.B => EditorTool.Redact,
            _ => null
        };
        if (tool is not null)
        {
            SelectTool(tool.Value);
            e.Handled = true;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (RenameLayer.Visibility == Visibility.Visible)
            return;

        if (SettingsLayer.Visibility == Visibility.Visible)
        {
            CloseSettings();
            return;
        }

        HotkeysSetting.IsChecked = _preferences.Hotkeys;
        EndCrop(returnFocus: false);
        HistorySetting.IsChecked = _preferences.KeepHistory;
        PrintScreenSetting.IsChecked = _preferences.PrintScreen;
        RefreshStartupSetting();
        RefreshStorageSetting();
        RefreshWorkspaceShortcut();
        UpdateLanguageOptions();
        HomeView.IsEnabled = EditorView.IsEnabled = false;
        SettingsLayer.Visibility = Visibility.Visible;
        HotkeysSetting.Focus();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void CloseSettings()
    {
        if (_recordingWorkspaceShortcut)
            StopRecordingWorkspaceShortcut();
        StopRecordingCaptureShortcut();
        SettingsLayer.Visibility = Visibility.Collapsed;
        ClearConfirmation.Visibility = Visibility.Collapsed;
        HomeView.IsEnabled = EditorView.IsEnabled = true;
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        var desired = _preferences with
        {
            Hotkeys = HotkeysSetting.IsChecked == true,
            KeepHistory = HistorySetting.IsChecked == true,
            PrintScreen = PrintScreenSetting.IsChecked == true
        };
        if (TrySavePreferences(desired))
        {
            ApplyHotkeys();
            RefreshHistory(_recent);
        }
        else
        {
            HotkeysSetting.IsChecked = _preferences.Hotkeys;
            HistorySetting.IsChecked = _preferences.KeepHistory;
            PrintScreenSetting.IsChecked = _preferences.PrintScreen;
        }
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton { Tag: string language }
            && language != UiText.Language && TrySavePreferences(_preferences with { Language = language }))
        {
            ApplyInterfaceLanguage(language);
        }
        UpdateLanguageOptions();
    }

    private void ApplyInterfaceLanguage(string language)
    {
        UiText.Apply(language);
        Language = XmlLanguage.GetLanguage(UiText.Culture.Name);
        UpdateHotkeyLabels();
        _historyCards.Clear();
        RefreshHistory(_recent);
        RefreshOcrContactLabels();
        if (_tray?.ContextMenuStrip is TrayMenu menu)
            menu.UpdateLanguage();
        Report(UiText.Get(EditorView.Visibility == Visibility.Visible ? "CaptureReady" : "Ready"));
    }

    private void UpdateLanguageOptions()
    {
        RussianInterface.IsChecked = UiText.Language == "ru";
        EnglishInterface.IsChecked = UiText.Language == "en";
    }

    private bool TrySavePreferences(Preferences desired)
    {
        try
        {
            _history.SavePreferences(desired);
            _preferences = desired;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(UiText.Get("SettingsWriteFailure"), true);
            return false;
        }
    }

    private async void OpenHistoryFolder_Click(object sender, RoutedEventArgs e) =>
        await RunOperation(() =>
        {
            Directory.CreateDirectory(_history.Folder);
            Process.Start(new ProcessStartInfo(_history.Folder) { UseShellExecute = true });
            return Task.CompletedTask;
        });

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => ClearConfirmation.Visibility = Visibility.Visible;
    private void CancelClearHistory_Click(object sender, RoutedEventArgs e) => ClearConfirmation.Visibility = Visibility.Collapsed;

    private async void ConfirmClearHistory_Click(object sender, RoutedEventArgs e)
    {
        await RunOperation(async () =>
        {
            await EnsureHistoryLoadedAsync();
            var cutoff = DateTime.UtcNow;
            if (!TrySavePreferences(_preferences with { HistoryClearedUtc = cutoff }))
                return;
            await Task.Run(() => _history.ForgetHistory(cutoff));
            _entryPath = null;
            _lastCapturePath = null;
            _lastCaptureImage = null;
            _savedRevision = _revision;
            ClearConfirmation.Visibility = Visibility.Collapsed;
            RefreshHistory([]);
            Report(UiText.Get("HistoryCleared"));
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is not null)
            MaximizeGlyph.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(2, 2, 1, 1) : new Thickness(1);
        ObserveWorkspaceState();
    }

    internal void OpenFromSecondLaunch()
    {
        if (!_busy)
            ShowWorkspace();
    }

    private void ShowWorkspace()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _workspaceWasMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    private void Report(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusDot.Fill = error ? new SolidColorBrush(Color.FromRgb(255, 131, 129)) : (Brush)FindResource("Accent");
    }

    private static async Task AccessClipboardAsync(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException) when (attempt < 3)
            {
                await Task.Delay(80);
            }
        }
    }

    private async Task RunOperation(Func<Task> action)
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            Report(ex switch
            {
                UnauthorizedAccessException => UiText.Get("DirectoryDenied"),
                IOException => UiText.Get("FileFailure"),
                UserFacingException known => UiText.Get(known.ResourceKey),
                ExternalException => UiText.Get("WindowsOperationFailure"),
                _ => UiText.Get("OperationFailure")
            }, true);
        }
        finally
        {
            _busy = false;
            CompleteCaptureRequest();
            RunPendingQuickCapture();
        }
    }
}


