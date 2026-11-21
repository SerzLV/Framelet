using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace Framelet;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _requestListener;

    internal IntPtr InitialForeground { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var startInTray = e.Args.Any(arg => string.Equals(arg, LoginStartup.TrayArgument, StringComparison.OrdinalIgnoreCase));
        _instance = new Mutex(false, @"Local\Framelet.Desktop.Instance", out var firstInstance);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Framelet.Desktop.Show");
        if (!firstInstance)
        {
            if (!startInTray)
                _showRequest.Set();
            Shutdown();
            return;
        }

        InitialForeground = Native.GetForegroundWindow();
        base.OnStartup(e);
        var workspace = new MainWindow();
        MainWindow = workspace;
        _requestListener = ThreadPool.RegisterWaitForSingleObject(_showRequest,
            (_, _) => Dispatcher.InvokeAsync(workspace.OpenFromSecondLaunch), null, Timeout.Infinite, false);
        if (startInTray)
            new WindowInteropHelper(workspace).EnsureHandle();
        else
            workspace.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        OcrService.Shutdown();
        _requestListener?.Unregister(null);
        _showRequest?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
