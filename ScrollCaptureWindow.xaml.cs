using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Framelet;

internal partial class ScrollCaptureWindow : Window
{
    private readonly Drawing.Rectangle _selection;
    private readonly ScrollStitcher _stitcher;
    private readonly ScrollWheelTarget _scrollTarget;
    private readonly FrameworkElement[] _tooltipOwners;
    private readonly TaskCompletionSource<BitmapSource?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _autoScrolling;
    private bool _stopAutoScroll;
    private bool _atLimit;
    private bool _canAddTail;
    private bool _tailAdded;
    private int? _documentBottom;
    private bool _capturing;
    private bool _closed;
    private bool _movingPanel;
    private bool _positionedByUser;
    private int _panelMoveCount;

    internal ScrollCaptureWindow(ScreenSnapshot selection, ScrollStitcher stitcher, ScrollWheelTarget scrollTarget, string? shortcut)
    {
        _selection = selection.Bounds;
        _stitcher = stitcher;
        _scrollTarget = scrollTarget;
        InitializeComponent();
        _tooltipOwners = [CancelButton, FramePreview, AutoScrollButton];
        LastFrame.Source = selection.Image;
        CaptureProgress.Text = UiText.Format("ScrollFrameCount", _stitcher.FrameCount, _stitcher.Height);
        CaptureInstructions.Text = shortcut is null ? UiText.Get("ScrollAddInstruction") : UiText.Format("ScrollAddWithShortcut", shortcut);
        Closed += (_, _) =>
        {
            _closed = true;
            _result.TrySetResult(null);
        };
    }

    internal async Task<BitmapSource?> RunAsync()
    {
        try
        {
            Show();
            MoveOutsideSelection();
            return await _result.Task;
        }
        finally
        {
            if (!_closed)
                Close();
        }
    }

    private async void AddFrame_Click(object sender, RoutedEventArgs e) => await CaptureNextFrameAsync();

    internal async Task CaptureNextFrameAsync()
    {
        if (_autoScrolling)
        {
            _stopAutoScroll = true;
            return;
        }

        if (!_capturing && !_closed && !_atLimit && !_tailAdded)
            await CaptureFrameAsync(automatic: false);
    }

    private async void AutoScroll_Click(object sender, RoutedEventArgs e)
    {
        if (_autoScrolling)
        {
            _stopAutoScroll = true;
            return;
        }
        if (_capturing || _closed || _atLimit || _tailAdded)
            return;

        _autoScrolling = true;
        _stopAutoScroll = false;
        _canAddTail = false;
        _documentBottom = null;
        UpdateActions();
        CaptureStatus.Text = UiText.Get("ScrollAutoRunning");
        try
        {
            if (!_scrollTarget.TryActivate())
            {
                CaptureStatus.Text = UiText.Get("ScrollAutoWindow");
                return;
            }

            // Allow focus to move from the capture controls to the selected window.
            await Task.Delay(120);
            const int endConfirmationAttempts = 3;
            const int maximumUnchangedAttempts = 5;
            var unchangedAttempts = 0;
            while (!_closed && !_stopAutoScroll)
            {
                await WaitForPanelInteractionAsync();
                if (_closed || _stopAutoScroll)
                    break;
                if (!await ScrollNextAsync(unchangedAttempts))
                {
                    if (!_closed)
                        CaptureStatus.Text = UiText.Get(_stopAutoScroll ? "ScrollAutoPaused" : "ScrollAutoWindow");
                    return;
                }
                var result = await CaptureFrameAsync(automatic: true);
                if (result == ScrollFrameResult.Added)
                {
                    unchangedAttempts = 0;
                }
                else if (result == ScrollFrameResult.Unchanged && !_closed && !_stopAutoScroll)
                {
                    // Try other points if a nested control or a delayed render consumed the wheel event.
                    unchangedAttempts++;
                    if ((unchangedAttempts == endConfirmationAttempts || unchangedAttempts == maximumUnchangedAttempts)
                        && await FinishAutoScrollAsync(allowRetry: unchangedAttempts < maximumUnchangedAttempts))
                    {
                        return;
                    }
                }
                else
                {
                    if (!_closed && _stopAutoScroll && result is not ScrollFrameResult.Limit)
                        CaptureStatus.Text = UiText.Get("ScrollAutoPaused");
                    return;
                }

                if (!_stopAutoScroll && !_closed)
                    await Task.Delay(150);
            }
            if (!_closed)
                CaptureStatus.Text = UiText.Get("ScrollAutoPaused");
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
        }
        finally
        {
            _autoScrolling = false;
            if (!_closed)
            {
                UpdateActions();
                if (_canAddTail)
                {
                    UpdateLayout();
                    MoveOutsideSelection();
                }
            }
        }
    }

    private async Task<bool> ScrollNextAsync(int pointOffset)
    {
        if (await _scrollTarget.TryScrollDownAsync(pointOffset))
            return true;
        if (_closed || _stopAutoScroll || !_scrollTarget.IsForeground || !PanelIntersects(_selection))
            return false;

        return await WithoutPanelAsync(
            () => _stopAutoScroll ? Task.FromResult(false) : _scrollTarget.TryScrollDownAsync(pointOffset),
            canceled: false);
    }

    private async Task<bool> FinishAutoScrollAsync(bool allowRetry)
    {
        await WaitForPanelInteractionAsync();
        var end = PanelIntersects(_selection)
            ? await WithoutPanelAsync(() => _scrollTarget.GetDocumentEndAsync(_selection), canceled: null)
            : await _scrollTarget.GetDocumentEndAsync(_selection);
        await WaitForPanelInteractionAsync();
        if (_closed)
            return true;
        if (_stopAutoScroll)
        {
            CaptureStatus.Text = UiText.Get("ScrollAutoPaused");
            return true;
        }
        if (!_scrollTarget.IsForeground)
        {
            CaptureStatus.Text = UiText.Get("ScrollAutoWindow");
            return true;
        }
        if (end is { AtEnd: false })
        {
            if (allowRetry)
                return false;
            CaptureStatus.Text = UiText.Get("ScrollAutoStalled");
            return true;
        }

        _documentBottom = end?.Bottom;
        var bottom = _documentBottom ?? _scrollTarget.GetVisibleBottom(_selection);
        _canAddTail = bottom - _selection.Bottom >= 3;
        if (end.HasValue && _canAddTail)
        {
            var bounds = Drawing.Rectangle.FromLTRB(_selection.Left, _selection.Top, _selection.Right, bottom);
            var frame = await CaptureUnobstructedAsync(bounds);
            if (_closed)
                return true;
            if (frame is not null && !_stopAutoScroll && _scrollTarget.IsForeground)
            {
                var result = await Task.Run(() => _stitcher.TrySetTail(frame));
                if (_closed)
                    return true;
                if (result == ScrollFrameResult.Added)
                {
                    SetTailAdded("ScrollTailAutoAdded");
                    return true;
                }
            }
        }

        CaptureStatus.Text = UiText.Get(_stopAutoScroll ? "ScrollAutoPaused" : _canAddTail ? "ScrollAutoEndWithTail" : "ScrollAutoEnd");
        return true;
    }

    private async void AddTail_Click(object sender, RoutedEventArgs e)
    {
        if (!_canAddTail || _autoScrolling || _capturing || _closed)
            return;

        _capturing = true;
        UpdateActions();
        try
        {
            if (!_scrollTarget.TryActivate())
            {
                CaptureStatus.Text = UiText.Get("ScrollAutoWindow");
                return;
            }

            Hide();
            await Task.Delay(90);
            if (_closed)
                return;
            if (!_scrollTarget.IsForeground)
            {
                CaptureStatus.Text = UiText.Get("ScrollAutoWindow");
                return;
            }

            var bottom = Math.Min(_documentBottom ?? int.MaxValue, _scrollTarget.GetVisibleBottom(_selection));
            var height = bottom - _selection.Bottom;
            if (height < 3)
            {
                _canAddTail = false;
                CaptureStatus.Text = UiText.Get("ScrollTailUnavailable");
                return;
            }

            var screen = Forms.Screen.FromRectangle(_selection).Bounds;
            Native.DwmFlush();
            var image = await Task.Run(() => CaptureService.Capture(screen));
            if (_closed)
                return;

            var extension = new Int32Rect(_selection.X - screen.X, _selection.Bottom - screen.Y, _selection.Width, height);
            var selection = await SelectTailAsync(new ScreenSnapshot(screen, image), extension);
            if (_closed || selection is not { } strip)
                return;

            var region = new Int32Rect(strip.X, _selection.Y - screen.Y, strip.Width, _selection.Height + strip.Height);
            var result = await Task.Run(() => _stitcher.TrySetTail(CaptureService.Crop(image, region)));
            if (_closed)
                return;
            if (result == ScrollFrameResult.Added)
            {
                SetTailAdded("ScrollTailAdded");
                return;
            }
            CaptureStatus.Text = UiText.Get(result == ScrollFrameResult.Limit ? "ScrollTailLimit" : "ScrollTailChanged");
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
        }
        finally
        {
            _capturing = false;
            if (!_closed)
            {
                UpdateActions();
                if (!IsVisible)
                    Show();
                UpdateLayout();
                MoveOutsideSelection();
            }
        }
    }

    private void SetTailAdded(string status)
    {
        _tailAdded = true;
        CaptureProgress.Text = UiText.Format("ScrollFrameCount", _stitcher.FrameCount, _stitcher.Height);
        CaptureStatus.Text = UiText.Get(status);
    }

    private static async Task<Int32Rect?> SelectTailAsync(ScreenSnapshot snapshot, Int32Rect extension)
    {
        var pending = new TaskCompletionSource<Int32Rect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = new SelectionWindow(snapshot, true, "ScrollTailPrompt",
            (_, region) => pending.TrySetResult(region), () => pending.TrySetResult(null), extension);
        var closed = false;
        overlay.Closed += (_, _) =>
        {
            closed = true;
            pending.TrySetResult(null);
        };
        try
        {
            overlay.Show();
            return await pending.Task;
        }
        finally
        {
            if (!closed)
                overlay.Close();
        }
    }

    private async Task<ScrollFrameResult?> CaptureFrameAsync(bool automatic)
    {
        _capturing = true;
        UpdateActions();
        try
        {
            if (!automatic)
                CaptureStatus.Text = UiText.Get("ScrollAligning");
            var attempt = await TryCaptureFrameAsync(automatic ? 300 : 200, automatic);
            var retries = automatic ? 2 : 1;
            for (var retry = 0; retry < retries && !_closed && (!automatic || !_stopAutoScroll)
                && attempt?.Result is (ScrollFrameResult.NoOverlap or ScrollFrameResult.Unchanged); retry++)
            {
                attempt = await TryCaptureFrameAsync(retry == 0 ? 220 : 420, automatic);
            }
            if (_closed || attempt is not { } capture)
                return null;

            var (frame, result) = capture;
            if (result == ScrollFrameResult.Added)
            {
                LastFrame.Source = frame;
                CaptureProgress.Text = UiText.Format("ScrollFrameCount", _stitcher.FrameCount, _stitcher.Height);
            }
            var status = result switch
            {
                ScrollFrameResult.Added => automatic ? "ScrollAutoRunning" : "ScrollFrameAdded",
                ScrollFrameResult.Unchanged => automatic ? "ScrollAutoRunning" : "ScrollNoChange",
                ScrollFrameResult.Limit => "ScrollLimit",
                _ => "ScrollNoOverlap"
            };
            CaptureStatus.Text = UiText.Get(status);
            if (result == ScrollFrameResult.Limit)
                _atLimit = true;
            if (!automatic)
                MoveOutsideSelection();
            return result;
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
            return null;
        }
        finally
        {
            _capturing = false;
            if (!_closed)
                UpdateActions();
        }
    }

    private async Task<(BitmapSource Frame, ScrollFrameResult Result)?> TryCaptureFrameAsync(int settleMilliseconds, bool automatic)
    {
        await Task.Delay(settleMilliseconds);
        if (automatic)
            await WaitForPanelInteractionAsync();
        if (_closed)
            return null;

        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        if (automatic && !_scrollTarget.IsForeground && !(_stopAutoScroll && Native.GetForegroundWindow() == handle))
        {
            CaptureStatus.Text = UiText.Get("ScrollAutoWindow");
            return null;
        }

        var frame = await CaptureUnobstructedAsync(_selection);
        return frame is null ? null : (frame, await Task.Run(() => _stitcher.TryAppend(frame)));
    }

    private async Task<BitmapSource?> CaptureUnobstructedAsync(Drawing.Rectangle bounds)
    {
        while (!_closed)
        {
            await WaitForPanelInteractionAsync();
            if (_closed)
                break;

            var moveCount = _panelMoveCount;
            var frame = PanelIntersects(bounds)
                ? await WithoutPanelAsync(CaptureAsync, canceled: null)
                : await CaptureAsync();
            if (moveCount == _panelMoveCount)
                return frame;
        }
        return null;

        async Task<BitmapSource?> CaptureAsync()
        {
            Native.DwmFlush();
            return await Task.Run(() => CaptureService.Capture(bounds));
        }
    }

    private bool PanelIntersects(Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(this).Handle;
        return !Native.GetWindowRect(handle, out var panel) ||
            bounds.IntersectsWith(Drawing.Rectangle.FromLTRB(panel.Left, panel.Top, panel.Right, panel.Bottom));
    }

    private async Task<T> WithoutPanelAsync<T>(Func<Task<T>> action, T canceled)
    {
        if (_closed)
            return canceled;
        Hide();
        try
        {
            await Task.Delay(50);
            return _closed ? canceled : await action();
        }
        finally
        {
            if (!_closed)
                Show();
        }
    }

    private async Task WaitForPanelInteractionAsync()
    {
        while (!_closed && (_movingPanel || (!_stopAutoScroll && IsActive && Mouse.LeftButton == MouseButtonState.Pressed)))
            await Task.Delay(40);
    }

    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        e.Handled = true;
        var handle = new WindowInteropHelper(this).Handle;
        var haveStart = Native.GetWindowRect(handle, out var start);
        _movingPanel = true;
        _panelMoveCount++;
        UpdateToolTips();
        try
        {
            DragMove();
        }
        finally
        {
            _movingPanel = false;
            if (!_closed)
            {
                if (haveStart && Native.GetWindowRect(handle, out var end)
                    && (start.Left != end.Left || start.Top != end.Top))
                    _positionedByUser = true;
                if (_autoScrolling && !_stopAutoScroll && IsActive)
                    _scrollTarget.TryActivate();
                UpdateToolTips();
            }
        }
    }

    private void UpdateToolTips()
    {
        var enabled = !_capturing && !_autoScrolling && !_movingPanel;
        foreach (var owner in _tooltipOwners)
        {
            ToolTipService.SetIsEnabled(owner, enabled);
            if (!enabled && owner.ToolTip is ToolTip { IsOpen: true } tooltip)
                tooltip.IsOpen = false;
        }
    }

    private void UpdateActions()
    {
        AddFrame.IsEnabled = !_capturing && !_autoScrolling && !_atLimit && !_tailAdded;
        FinishButton.IsEnabled = !_capturing && !_autoScrolling;
        AutoScrollButton.IsEnabled = _autoScrolling || (!_capturing && !_atLimit && !_tailAdded);
        CaptureInstructions.Visibility = _canAddTail ? Visibility.Collapsed : Visibility.Visible;
        TailButton.Visibility = _canAddTail ? Visibility.Visible : Visibility.Collapsed;
        TailButton.IsEnabled = !_capturing && !_autoScrolling && !_atLimit;
        TailButton.SetResourceReference(ContentControl.ContentProperty, UiText.Key(_tailAdded ? "ScrollTailAdjust" : "ScrollTailAdd"));
        AutoScrollLabel.SetResourceReference(TextBlock.TextProperty, UiText.Key(_autoScrolling ? "ScrollAutoStop" : "ScrollAuto"));
        UpdateToolTips();
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing || _autoScrolling)
            return;

        _capturing = true;
        IsEnabled = false;
        try
        {
            _result.TrySetResult(await Task.Run(_stitcher.Finish));
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void MoveOutsideSelection()
    {
        if (_positionedByUser)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        var area = Forms.Screen.FromRectangle(_selection).WorkingArea;
        if (!Native.GetWindowRect(handle, out var size))
            return;

        const int gap = 10;
        var width = size.Right - size.Left;
        var height = size.Bottom - size.Top;
        var x = Math.Clamp(_selection.Right - width, area.Left, Math.Max(area.Left, area.Right - width));
        var y = Math.Clamp(_selection.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        var top = _selection.Top - height - gap;
        var bottom = _selection.Bottom + gap;
        var left = _selection.Left - width - gap;
        var right = _selection.Right + gap;
        var position = bottom + height <= area.Bottom ? new Drawing.Point(x, bottom)
            : top >= area.Top ? new Drawing.Point(x, top)
            : right + width <= area.Right ? new Drawing.Point(right, y)
            : left >= area.Left ? new Drawing.Point(left, y)
            : new Drawing.Point(Math.Max(area.Left, area.Right - width - gap), Math.Max(area.Top, area.Bottom - height - gap));
        const uint noActivateOrResize = 0x0010 | 0x0001 | 0x0004;
        Native.SetWindowPos(handle, IntPtr.Zero, position.X, position.Y, 0, 0, noActivateOrResize);
    }
}
