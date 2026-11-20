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
    private readonly TaskCompletionSource<BitmapSource?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _autoScrolling;
    private bool _stopAutoScroll;
    private bool _atLimit;
    private bool _capturing;
    private bool _closed;

    internal ScrollCaptureWindow(ScreenSnapshot selection, ScrollStitcher stitcher, ScrollWheelTarget scrollTarget, string? shortcut)
    {
        _selection = selection.Bounds;
        _stitcher = stitcher;
        _scrollTarget = scrollTarget;
        InitializeComponent();
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

        if (!_capturing && !_closed && !_atLimit)
            await CaptureFrameAsync(automatic: false);
    }

    private async void AutoScroll_Click(object sender, RoutedEventArgs e)
    {
        if (_autoScrolling)
        {
            _stopAutoScroll = true;
            return;
        }
        if (_capturing || _closed || _atLimit)
            return;

        _autoScrolling = true;
        _stopAutoScroll = false;
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
            while (!_closed && !_stopAutoScroll)
            {
                await WaitForPanelClickAsync();
                if (_closed || _stopAutoScroll)
                    break;
                if (!await _scrollTarget.TryScrollDownAsync())
                {
                    CaptureStatus.Text = UiText.Get("ScrollAutoWindow");
                    return;
                }
                var result = await CaptureFrameAsync(automatic: true);
                if (result != ScrollFrameResult.Added)
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
                UpdateActions();
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
            if (!_closed && (!automatic || !_stopAutoScroll) && attempt?.Result is (ScrollFrameResult.NoOverlap or ScrollFrameResult.Unchanged))
                attempt = await TryCaptureFrameAsync(220, automatic);
            if (_closed || attempt is not { } capture)
                return null;

            var (frame, result) = capture;
            if (result == ScrollFrameResult.Added)
            {
                LastFrame.Source = frame;
                CaptureProgress.Text = UiText.Format("ScrollFrameCount", _stitcher.FrameCount, _stitcher.Height);
            }
            CaptureStatus.Text = result switch
            {
                ScrollFrameResult.Added => UiText.Get(automatic ? "ScrollAutoRunning" : "ScrollFrameAdded"),
                ScrollFrameResult.Unchanged => UiText.Get(automatic ? "ScrollAutoEnd" : "ScrollNoChange"),
                ScrollFrameResult.Limit => UiText.Get("ScrollLimit"),
                _ => UiText.Get("ScrollNoOverlap")
            };
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
            await WaitForPanelClickAsync();
        if (_closed)
            return null;

        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        if (automatic && !_scrollTarget.IsForeground && !(_stopAutoScroll && Native.GetForegroundWindow() == handle))
        {
            CaptureStatus.Text = UiText.Get("ScrollAutoWindow");
            return null;
        }

        var hidePanel = !Native.GetWindowRect(handle, out var panel) ||
            _selection.IntersectsWith(Drawing.Rectangle.FromLTRB(panel.Left, panel.Top, panel.Right, panel.Bottom));
        BitmapSource frame;
        try
        {
            if (hidePanel)
            {
                Hide();
                await Task.Delay(50);
                if (_closed)
                    return null;
            }

            Native.DwmFlush();
            frame = await Task.Run(() => CaptureService.Capture(_selection));
        }
        finally
        {
            if (hidePanel && !_closed)
                Show();
        }

        return (frame, await Task.Run(() => _stitcher.TryAppend(frame)));
    }

    private async Task WaitForPanelClickAsync()
    {
        while (!_closed && !_stopAutoScroll && IsActive && Mouse.LeftButton == MouseButtonState.Pressed)
            await Task.Delay(40);
    }

    private void UpdateActions()
    {
        AddFrame.IsEnabled = !_capturing && !_autoScrolling && !_atLimit;
        FinishButton.IsEnabled = !_capturing && !_autoScrolling;
        AutoScrollButton.IsEnabled = _autoScrolling || (!_capturing && !_atLimit);
        AutoScrollLabel.SetResourceReference(TextBlock.TextProperty, UiText.Key(_autoScrolling ? "ScrollAutoStop" : "ScrollAuto"));
        AutoScrollButton.SetResourceReference(ToolTipProperty, UiText.Key(_autoScrolling ? "ScrollAutoStop" : "ScrollAutoTip"));
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
