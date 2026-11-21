using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Framelet;

public partial class MainWindow
{
    private HistoryItem? _renameTarget;
    private Action<bool>? _historyConfirmation;
    private readonly Dictionary<HistoryItem, string> _historyText = new(ReferenceEqualityComparer.Instance);
    private HashSet<HistoryItem> _historyMembership = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _historyIndexRequest;
    private bool _historyIndexUnavailable;

    private void HomeScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource != HomeView || HistoryHeaderOffset is null)
            return;

        var top = HistoryHeader.TranslatePoint(new Point(), HomeView).Y - HistoryHeaderOffset.Y;
        HistoryHeaderOffset.Y = Math.Max(0, -top);
        RequestHistoryLayout();
    }

    private void OpenHistoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Button { ContextMenu: { } menu } } anchor)
            return;

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void HistoryMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
            menu.ClearValue(ContextMenu.PlacementProperty);
        }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: HistoryItem item })
        {
            await RunOperation(async () =>
            {
                var document = await Task.Run(() => _history.OpenEditorDocument(item.FilePath));
                var origin = item.FilePath == _lastCapturePath ? _lastCaptureOrigin : null;
                OpenEditor(document.Image, item.FilePath, document.Journal, origin);
                Report(UiText.Get("OpenedHistory"));
            });
        }
    }

    private async void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: HistoryItem item })
        {
            await RunOperation(async () =>
            {
                var image = await Task.Run(() => HistoryStore.OpenImage(item.FilePath));
                await CopyImageToClipboardAsync(image);
                Report(UiText.Get("ImageCopied"));
            });
        }
    }

    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: HistoryItem item })
            return;
        if (!await AskHistoryConfirmationAsync("Delete", UiText.Format("RecycleConfirm", item.DisplayName),
                "RecycleAction", HistorySearch))
            return;

        await RunOperation(async () =>
        {
            try
            {
                await Task.Run(() => _history.Delete(item.FilePath));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            RefreshHistory(_recent.Where(entry => !string.Equals(entry.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                .ToArray());
            Report(UiText.Get("HistoryItemDeleted"));
        });
    }

    private Task<bool> AskHistoryConfirmationAsync(string title, string message, string action, UIElement restoreFocus)
    {
        if (_busy || _historyConfirmation is not null)
            return Task.FromResult(false);

        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var homeEnabled = HomeView.IsEnabled;
        var editorEnabled = EditorView.IsEnabled;
        var settingsEnabled = SettingsLayer.IsEnabled;

        _historyConfirmation = confirmed =>
        {
            _historyConfirmation = null;
            HistoryConfirmationLayer.Visibility = Visibility.Collapsed;
            HomeView.IsEnabled = homeEnabled;
            EditorView.IsEnabled = editorEnabled;
            SettingsLayer.IsEnabled = settingsEnabled;
            if (restoreFocus.IsVisible && IsActive)
                restoreFocus.Focus();
            confirmation.SetResult(confirmed);
        };

        HistoryConfirmationTitle.Text = UiText.Get(title);
        HistoryConfirmationMessage.Text = message;
        HistoryConfirmationAction.Content = UiText.Get(action);
        HomeView.IsEnabled = EditorView.IsEnabled = SettingsLayer.IsEnabled = false;
        HistoryConfirmationLayer.Visibility = Visibility.Visible;
        HistoryConfirmationCancel.Focus();
        return confirmation.Task;
    }

    private void ConfirmHistoryAction_Click(object sender, RoutedEventArgs e) => _historyConfirmation?.Invoke(true);
    private void CancelHistoryAction_Click(object sender, RoutedEventArgs e) => CancelHistoryConfirmation();
    private void CancelHistoryConfirmation() => _historyConfirmation?.Invoke(false);

    private void RenameHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not MenuItem { DataContext: HistoryItem item })
            return;

        _renameTarget = item;
        HistoryNewName.Text = Path.GetFileNameWithoutExtension(item.DisplayName);
        HistoryRenameError.Visibility = Visibility.Collapsed;
        HomeView.IsEnabled = false;
        RenameLayer.Visibility = Visibility.Visible;
        HistoryNewName.Focus();
        HistoryNewName.SelectAll();
    }

    private async void ConfirmRename_Click(object sender, RoutedEventArgs e)
    {
        if (_renameTarget is not { } item)
            return;

        await RunOperation(async () =>
        {
            var name = HistoryNewName.Text;
            RenameLayer.IsEnabled = false;
            try
            {
                var (renamed, error) = await Task.Run(() => _history.Rename(item, name));
                if (renamed is null)
                {
                    ShowRenameError(error ?? "HistoryRenameFailure");
                    return;
                }

                if (_entryPath == item.FilePath)
                    _entryPath = renamed.FilePath;
                if (_lastCapturePath == item.FilePath)
                    _lastCapturePath = renamed.FilePath;
                if (_historyText.Remove(item, out var text))
                    _historyText[renamed] = text;
                RefreshHistory(_recent.Select(entry => entry.FilePath == item.FilePath ? renamed : entry).ToArray());
                CloseRename();
                Report(UiText.Format("HistoryRenamed", renamed.DisplayName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine(ex);
                ShowRenameError("HistoryRenameFailure");
            }
            finally
            {
                RenameLayer.IsEnabled = true;
                if (RenameLayer.Visibility == Visibility.Visible)
                    HistoryNewName.Focus();
            }
        });
    }

    private void ShowRenameError(string key)
    {
        HistoryRenameError.Text = UiText.Get(key);
        HistoryRenameError.Visibility = Visibility.Visible;
    }

    private void HistoryNewName_Changed(object sender, TextChangedEventArgs e)
    {
        if (HistoryRenameError is not null)
            HistoryRenameError.Visibility = Visibility.Collapsed;
    }

    private void CancelRename_Click(object sender, RoutedEventArgs e) => CloseRename();

    private void CloseRename(bool returnFocus = true)
    {
        if (_renameTarget is null)
            return;

        _renameTarget = null;
        RenameLayer.Visibility = Visibility.Collapsed;
        HomeView.IsEnabled = true;
        if (returnFocus)
            HistorySearch.Focus();
    }

    private void RefreshHistory(IReadOnlyList<HistoryItem> items)
    {
        _recent = items;
        _historyMembership = new HashSet<HistoryItem>(items, ReferenceEqualityComparer.Instance);
        foreach (var entry in _historyText.Keys.Where(entry => !_historyMembership.Contains(entry)).ToArray())
            _historyText.Remove(entry);
        RemoveOldThumbnails();
        ClearHistoryButton.IsEnabled = items.Count > 0;
        FilterHistory();
        UpdateHistoryIndex();
    }

    private void FilterHistory()
    {
        var query = HistorySearch.Text.Trim();
        var contentQuery = NormalizeHistorySearchText(query);
        var searching = query.Length > 0;
        var visible = searching
            ? _recent.Where(entry => entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (_historyText.TryGetValue(entry, out var text) && text.Contains(contentQuery, StringComparison.OrdinalIgnoreCase))).ToArray()
            : _recent;
        _filteredHistory = visible;
        RequestHistoryLayout();
        HistorySearchClear.Visibility = HistorySearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHistory.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCount.Text = searching ? $"{visible.Count:N0} / {_recent.Count:N0}" : _recent.Count == 0 ? "" : $"{_recent.Count:N0}";
        var indexing = searching && !_historyIndexUnavailable && _historyText.Count < _recent.Count;
        var progress = indexing ? UiText.Format("HistoryIndexProgress", _historyText.Count, _recent.Count) : "";
        HistoryIndexStatus.Visibility = searching && ((indexing && visible.Count > 0) || (_historyIndexUnavailable && _historyText.Count < _recent.Count))
            ? Visibility.Visible : Visibility.Collapsed;
        HistoryIndexStatus.Text = indexing ? progress : UiText.Get("HistoryIndexUnavailable");
        EmptyTitle.Text = UiText.Get(indexing ? "HistorySearching" : searching ? "HistoryNoResults" : _preferences.KeepHistory ? "EmptyTitle" : "HistoryOff");
        EmptyDescription.Text = indexing ? progress : UiText.Get(searching ? "HistoryNoResultsDetail" : _preferences.KeepHistory ? "EmptyDescription" : "HistoryEnable");
    }

    private void HistorySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (HistoryItems is not null && HistoryIndexStatus is not null)
        {
            var header = HistoryHeader.TranslatePoint(new Point(), HomeView).Y - HistoryHeaderOffset.Y;
            if (header < 0)
                HomeView.ScrollToVerticalOffset(HomeView.VerticalOffset + header);
            FilterHistory();
            UpdateHistoryIndex();
        }
    }

    private void HistoryVisibility_Changed(object sender, DependencyPropertyChangedEventArgs e)
    {
        RequestHistoryLayout();
        UpdateHistoryIndex();
    }

    private void UpdateHistoryIndex()
    {
        if (HistorySearch is null || !HomeView.IsVisible || string.IsNullOrWhiteSpace(HistorySearch.Text))
        {
            var previous = _historyIndexRequest;
            _historyIndexRequest = null;
            previous?.Cancel();
            return;
        }

        if (_historyIndexRequest is null && !_historyIndexUnavailable && _historyText.Count < _recent.Count)
            _ = IndexHistoryTextAsync();
    }

    private async Task IndexHistoryTextAsync()
    {
        using var request = new CancellationTokenSource();
        _historyIndexRequest = request;
        try
        {
            await Task.Delay(300, request.Token);
            var refresh = Stopwatch.StartNew();
            foreach (var item in _recent)
            {
                request.Token.ThrowIfCancellationRequested();
                if (!_historyMembership.Contains(item) || _historyText.ContainsKey(item))
                    continue;

                string text;
                try
                {
                    text = await _history.ReadHistoryTextAsync(item.FilePath, request.Token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
                {
                    Trace.WriteLine(ex);
                    text = "";
                }

                request.Token.ThrowIfCancellationRequested();
                if (_historyMembership.Contains(item))
                {
                    _historyText[item] = NormalizeHistorySearchText(text);
                    if (refresh.ElapsedMilliseconds >= 120)
                    {
                        FilterHistory();
                        refresh.Restart();
                    }
                }
            }
            FilterHistory();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            if (_historyIndexRequest == request)
            {
                _historyIndexUnavailable = true;
                FilterHistory();
            }
        }
        finally
        {
            if (_historyIndexRequest == request)
            {
                _historyIndexRequest = null;
                UpdateHistoryIndex();
            }
        }
    }

    private static string NormalizeHistorySearchText(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private void HistorySearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || HistorySearch.Text.Length == 0)
            return;

        e.Handled = true;
        HistorySearch.Clear();
    }

    private void ClearHistorySearch_Click(object sender, RoutedEventArgs e)
    {
        HistorySearch.Clear();
        HistorySearch.Focus();
    }
}
