using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

public partial class App : Application
{
    /// <summary>Start without a window, in the notification area.</summary>
    public const string TrayArg = "--tray";

    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One instance per data folder: two would fight over hotkeys and game files.
        var name = "AchievementCompanion." +
                   Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(AppPaths.DataRoot.ToLowerInvariant())))[..16];
        _single = new Mutex(true, name, out bool first);
        if (!first)
        {
            _single.Dispose();
            _single = null;
            ActivateRunningCopy(name);
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += OnUnhandled;
        SessionEnding += (_, _) => (MainWindow as AC.App.MainWindow)?.AllowQuit();
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            AppLog.Error("Unhandled error", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            AppLog.Error("Background task error", a.Exception);
            a.SetObserved();
        };
        AppLog.Info($"Starting {typeof(App).Assembly.GetName().Version?.ToString(3)}, data: {AppPaths.DataRoot}");

        // Hardware rendering stops while the display is off; scripted screenshots switch to software rendering.
        if (Environment.GetEnvironmentVariable("AC_SOFTWARE_RENDERING") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var settings = new JsonStore().LoadSettings();
        Loc.SetLanguage(settings.UiLanguage);
        // Theme (built-in Fluent): set before any window so its dictionary is in the app resources.
        ThemeManager.Apply(settings.Theme);
        ImageCache.TrimInBackground();
        base.OnStartup(e);
        bool inTray = e.Args.Contains(TrayArg);
        var window = new MainWindow(inTray);
        if (!inTray) window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_single is not null)
        {
            try { _single.ReleaseMutex(); } catch (ApplicationException) { }
            _single.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>
    /// A second instance exits and asks the first to show its window. The first shows it:
    /// raising its own window from inside beats finding it from outside.
    /// </summary>
    private static void ActivateRunningCopy(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name + ".show", out var show)) return;
        using (show)
        {
            Native.AllowAnyForeground();
            show.Set();
        }
    }

    private void ShowMainWindow() => (MainWindow as AC.App.MainWindow)?.ShowFromTray();

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("UI error", e.Exception);
        MessageBox.Show(e.Exception.Message, Loc.T("dialog.errorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
