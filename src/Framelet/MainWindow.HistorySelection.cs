using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Framelet;

public partial class MainWindow
{
    private readonly DispatcherTimer _historyDragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private HistorySelectionGesture? _historyGesture;
    private string? _historyRangeAnchor;
    private bool IsDraggingHistorySelection => _historyGesture?.PreviousSelection is not null;

    private void SelectHistoryCard(HistoryItem item, ModifierKeys modifiers)
    {
        HistorySelectionToggle.IsChecked = true;
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            var target = FindHistoryIndex(item.FilePath);
            if (target < 0)
                return;

            var anchor = FindHistoryIndex(_historyRangeAnchor);
            if (anchor < 0)
            {
                anchor = target;
                _historyRangeAnchor = item.FilePath;
            }
            if (!modifiers.HasFlag(ModifierKeys.Control))
                _selectedHistoryPaths.Clear();
            for (var index = Math.Min(anchor, target); index <= Math.Max(anchor, target); index++)
                _selectedHistoryPaths.Add(_filteredHistory[index].FilePath);
        }
        else
        {
            _historyRangeAnchor = item.FilePath;
            if (!_selectedHistoryPaths.Add(item.FilePath))
                _selectedHistoryPaths.Remove(item.FilePath);
        }
        UpdateHistorySelection();
    }

    private int FindHistoryIndex(string? path)
    {
        if (path is not null)
        {
            for (var index = 0; index < _filteredHistory.Count; index++)
                if (string.Equals(_filteredHistory[index].FilePath, path, StringComparison.OrdinalIgnoreCase))
                    return index;
        }
        return -1;
    }

    private void ClearHistorySelection()
    {
        _selectedHistoryPaths.Clear();
        _historyRangeAnchor = null;
    }

    private void HistoryCardArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_busy || e.ClickCount != 1 || _filteredHistory.Count == 0)
            return;

        var button = HistoryButtonAt(e.OriginalSource as DependencyObject);
        if (button is not null && button.Name != "HistoryTile")
            return;

        if (button is null && !HistoryCardArea.CaptureMouse())
            return;

        var path = (button?.DataContext as HistoryCard)?.Item.FilePath;
        _historyGesture = new HistorySelectionGesture(e.GetPosition(HistoryCardArea), e.GetPosition(HomeView), Keyboard.Modifiers, path);
        e.Handled = button is null;
    }

    private Button? HistoryButtonAt(DependencyObject? source)
    {
        for (var element = source; element is not null && element != HistoryCardArea;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is Button button)
                return button;
        }
        return null;
    }

    private void HistoryCardArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (_historyGesture is not { } gesture || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (!IsDraggingHistorySelection)
        {
            var delta = e.GetPosition(HomeView) - gesture.ViewportStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            gesture.PreviousSelection = new HashSet<string>(_selectedHistoryPaths, _selectedHistoryPaths.Comparer);
            if (!HistoryCardArea.CaptureMouse())
            {
                FinishHistoryGesture(cancel: true);
                return;
            }
            HistoryMarquee.Visibility = Visibility.Visible;
            _historyDragScrollTimer.Start();
        }
        UpdateHistoryMarquee();
        e.Handled = true;
    }

    private void HistoryCardArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_historyGesture is not { } gesture)
            return;

        if (IsDraggingHistorySelection)
        {
            UpdateHistoryMarquee();
            e.Handled = true;
        }
        else if (HistoryCardArea.IsMouseCaptured && gesture.Modifiers == ModifierKeys.None && IsSelectingHistory)
        {
            ClearHistorySelection();
            UpdateHistorySelection();
        }
        FinishHistoryGesture(cancel: false);
    }

    private void HistoryCardArea_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == HistoryCardArea || (_historyGesture is not null && !IsDraggingHistorySelection))
            FinishHistoryGesture(cancel: true);
    }

    private void UpdateHistoryMarquee()
    {
        if (_historyGesture is not { PreviousSelection: { } previous } gesture)
            return;

        var viewportEnd = Mouse.GetPosition(HomeView);
        viewportEnd.Y = Math.Clamp(viewportEnd.Y, HistoryViewportTop(), HomeView.ViewportHeight);
        var end = HomeView.TranslatePoint(viewportEnd, HistoryCardArea);
        end.X = Math.Clamp(end.X, 0, HistoryCardArea.ActualWidth);
        end.Y = Math.Clamp(end.Y, 0, HistoryCardArea.ActualHeight);
        var bounds = new Rect(gesture.Start, end);
        bounds.Width = Math.Max(1, bounds.Width);
        bounds.Height = Math.Max(1, bounds.Height);
        Canvas.SetLeft(HistoryMarquee, bounds.X);
        Canvas.SetTop(HistoryMarquee, bounds.Y);
        HistoryMarquee.Width = bounds.Width;
        HistoryMarquee.Height = bounds.Height;

        var invert = gesture.Modifiers.HasFlag(ModifierKeys.Control);
        var extend = gesture.Modifiers.HasFlag(ModifierKeys.Shift);
        var paths = HistoryViewport.IntersectingCards(_filteredHistory.Count, HistoryCardArea.ActualWidth, bounds)
            .Select(index => _filteredHistory[index].FilePath);
        _selectedHistoryPaths.Clear();
        if (invert || extend)
            _selectedHistoryPaths.UnionWith(previous);
        if (invert && !extend)
            _selectedHistoryPaths.SymmetricExceptWith(paths);
        else
            _selectedHistoryPaths.UnionWith(paths);

        UpdateHistorySelection();
    }

    private void HistoryDragScroll_Tick(object? sender, EventArgs e)
    {
        const double edge = 34;
        var top = HistoryViewportTop();
        var bottom = HomeView.ViewportHeight;
        var y = Mouse.GetPosition(HomeView).Y;
        var distance = y > bottom - edge ? y - (bottom - edge) : Math.Min(0, y - (top + edge));
        if (distance == 0)
            return;

        var headerTop = HistoryHeader.TranslatePoint(new Point(), HomeView).Y - HistoryHeaderOffset.Y;
        var minimum = Math.Clamp(HomeView.VerticalOffset + Math.Min(0, headerTop), 0, HomeView.ScrollableHeight);
        var next = Math.Clamp(HomeView.VerticalOffset + Math.Clamp(distance * .55, -50, 50), minimum, HomeView.ScrollableHeight);
        if (next != HomeView.VerticalOffset)
            HomeView.ScrollToVerticalOffset(next);
    }

    private double HistoryViewportTop()
    {
        var headerBottom = HistoryHeader.TranslatePoint(new Point(0, HistoryHeader.ActualHeight), HomeView).Y;
        return Math.Clamp(headerBottom, 0, HomeView.ViewportHeight);
    }

    private void FinishHistoryGesture(bool cancel)
    {
        if (_historyGesture is not { } gesture)
            return;

        _historyGesture = null;
        _historyDragScrollTimer.Stop();
        HistoryMarquee.Visibility = Visibility.Collapsed;
        if (HistoryCardArea.IsMouseCaptured)
            HistoryCardArea.ReleaseMouseCapture();
        if (gesture.PreviousSelection is not { } previous)
            return;

        if (cancel)
        {
            _selectedHistoryPaths.Clear();
            _selectedHistoryPaths.UnionWith(previous);
        }
        else
        {
            _historyRangeAnchor = null;
            if (_selectedHistoryPaths.Count > 0)
            {
                HistorySelectionToggle.IsChecked = true;
                _historyRangeAnchor = gesture.CardPath is { } path && _selectedHistoryPaths.Contains(path) ? path
                    : _filteredHistory.FirstOrDefault(item => _selectedHistoryPaths.Contains(item.FilePath))?.FilePath;
            }
        }
        UpdateHistorySelection();
        if (!cancel && IsSelectingHistory)
            HistorySelectionToggle.Focus();
    }

    private sealed record HistorySelectionGesture(Point Start, Point ViewportStart, ModifierKeys Modifiers, string? CardPath)
    {
        internal HashSet<string>? PreviousSelection { get; set; }
    }
}
