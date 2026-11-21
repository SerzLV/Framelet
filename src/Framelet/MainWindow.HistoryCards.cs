using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Framelet;

internal enum HistoryCardSelection { Inactive, Unselected, Selected }

internal sealed class HistoryCard(HistoryItem item, HistoryCardSelection selection) : INotifyPropertyChanged
{
    private HistoryCardSelection _selection = selection;
    public HistoryItem Item { get; } = item;
    public HistoryCardSelection Selection
    {
        get => _selection;
        internal set
        {
            if (_selection == value)
                return;
            _selection = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selection)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private const int ThumbnailCacheSize = 96;
    private readonly ObservableCollection<HistoryCard> _historyCards = [];
    private readonly Dictionary<HistoryItem, LinkedListNode<(HistoryItem Item, BitmapSource Image)>> _historyThumbnails
        = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<(HistoryItem Item, BitmapSource Image)> _thumbnailOrder = [];
    private readonly Dictionary<Image, CancellationTokenSource> _thumbnailRequests = [];
    private readonly SemaphoreSlim _thumbnailWorkers = new(2);
    private IReadOnlyList<HistoryItem> _filteredHistory = [];
    private IReadOnlyList<HistoryItem>? _historyCardSource;
    private int _historyCardStart;
    private bool _historyLayoutPending;

    private void HistoryCardsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            RequestHistoryLayout();
    }

    private void RequestHistoryLayout()
    {
        if (_historyLayoutPending || HistoryCardsPanel is null || !HomeView.IsVisible)
            return;
        _historyLayoutPending = true;
        Dispatcher.InvokeAsync(UpdateHistoryCards, DispatcherPriority.Loaded);
    }

    private void UpdateHistoryCards()
    {
        _historyLayoutPending = false;
        if (!HomeView.IsVisible || HistoryCardsPanel.ActualWidth <= 0 || HomeView.ViewportHeight <= 0)
            return;

        var top = HistoryCardsPanel.TranslatePoint(new Point(), HomeView).Y;
        var (start, count, before, after) = HistoryViewport.VisibleRange(_filteredHistory.Count,
            HistoryCardsPanel.ActualWidth, HomeView.ViewportHeight, top);
        if (HistoryBeforeCards.Height != before)
            HistoryBeforeCards.Height = before;
        if (HistoryAfterCards.Height != after)
            HistoryAfterCards.Height = after;

        var end = start + count;
        if (!ReferenceEquals(_historyCardSource, _filteredHistory))
        {
            _historyCardSource = _filteredHistory;
            if (_historyCards.Count == count && _historyCards.Select((card, index) =>
                    ReferenceEquals(card.Item, _filteredHistory[start + index])).All(match => match))
            {
                _historyCardStart = start;
                return;
            }
            _historyCards.Clear();
        }

        if (count == 0 || _historyCardStart >= end || _historyCardStart + _historyCards.Count <= start)
            _historyCards.Clear();
        while (_historyCards.Count > 0 && _historyCardStart < start)
        {
            _historyCards.RemoveAt(0);
            _historyCardStart++;
        }
        while (_historyCardStart + _historyCards.Count > end && _historyCards.Count > 0)
            _historyCards.RemoveAt(_historyCards.Count - 1);
        if (_historyCards.Count == 0)
            _historyCardStart = start;
        while (_historyCardStart > start)
            _historyCards.Insert(0, CreateHistoryCard(_filteredHistory[--_historyCardStart]));
        for (var index = _historyCardStart + _historyCards.Count; index < end; index++)
            _historyCards.Add(CreateHistoryCard(_filteredHistory[index]));
    }

    private HistoryCard CreateHistoryCard(HistoryItem item) => new(item, GetHistoryCardSelection(item));

    private async void HistoryThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image { DataContext: HistoryItem item } image)
            return;
        if (_historyThumbnails.TryGetValue(item, out var cached))
        {
            _thumbnailOrder.Remove(cached);
            _thumbnailOrder.AddLast(cached);
            image.Source = cached.Value.Image;
            return;
        }

        using var request = new CancellationTokenSource();
        if (_thumbnailRequests.Remove(image, out var previous))
            previous.Cancel();
        _thumbnailRequests.Add(image, request);
        try
        {
            await _thumbnailWorkers.WaitAsync(request.Token);
            BitmapSource thumbnail;
            try
            {
                thumbnail = await Task.Run(() => HistoryStore.ReadThumbnail(item), request.Token);
            }
            finally
            {
                _thumbnailWorkers.Release();
            }
            if (request.IsCancellationRequested || !image.IsLoaded || !ReferenceEquals(image.DataContext, item)
                || !_historyMembership.Contains(item))
                return;

            if (!_historyThumbnails.ContainsKey(item))
            {
                if (_historyThumbnails.Count >= ThumbnailCacheSize && _thumbnailOrder.First is { } oldest)
                {
                    _historyThumbnails.Remove(oldest.Value.Item);
                    _thumbnailOrder.RemoveFirst();
                }
                _historyThumbnails.Add(item, _thumbnailOrder.AddLast((item, thumbnail)));
            }
            image.Source = thumbnail;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or FormatException)
        {
            Trace.WriteLine(error);
        }
        finally
        {
            if (_thumbnailRequests.TryGetValue(image, out var active) && ReferenceEquals(active, request))
                _thumbnailRequests.Remove(image);
        }
    }

    private void HistoryThumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image)
            return;
        if (_thumbnailRequests.Remove(image, out var request))
            request.Cancel();
        image.Source = null;
    }

    private void RemoveOldThumbnails()
    {
        foreach (var entry in _historyThumbnails.Keys.Where(entry => !_historyMembership.Contains(entry)).ToArray())
        {
            _thumbnailOrder.Remove(_historyThumbnails[entry]);
            _historyThumbnails.Remove(entry);
        }
    }
}
