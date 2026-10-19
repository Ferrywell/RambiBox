using System.Windows;
using System.Windows.Threading;
using RambiBox.App.Services;
using RambiBox.App.Views;
using RambiBox.Core;
using RambiBox.Core.Calibration;

namespace RambiBox.App;

/// <summary>
/// Composition root: single instance, settings, engine, tray icon and Windows integration.
/// The main window is created on demand and fully released when closed (the app then idles in the tray).
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\RambiBox.SingleInstance";
    private const string ShowEventName = @"Local\RambiBox.ShowWindow";
    private const string ExitEventName = @"Local\RambiBox.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private SystemIntegration? _system;
    private TrayIconService? _tray;
    private MainWindow? _main;
    private CalibrationWindow? _calibration;
    private bool _exiting;
    private int _uiErrors;
    private DateTime _uiErrorWindow = DateTime.MinValue;

    public AppHost Host { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    /// <summary>Page shown when the main window is opened again (kept for the session only).</summary>
    public string? LastPage { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = AppOptions.Parse(e.Args);
        // A simulated (development) instance may run next to the real one.
        string suffix = options.Simulate ? ".Simulate" : "";

        if (options.Exit)
        {
            // "RambiBox.exe --exit": ask a running instance to exit cleanly (LEDs off), e.g. before an update.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ExitEventName + suffix);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* not running */ }
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName + suffix, out bool createdNew);
        if (!createdNew)
        {
            // Already running: ask the first instance to show its window (unless we were started by autostart).
            if (!options.Tray)
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowEventName + suffix);
                    NativeMethods.AllowSetForegroundWindow(-1); // ASFW_ANY: let the other instance come to the front
                    ev.Set();
                }
                catch (Exception ex) { Log.Warn($"Could not signal the running instance: {ex.Message}"); }
            }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        var logs = new LogBuffer(); // first, so the Device page shows the whole session
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Log.Info($"RambiBox {typeof(App).Assembly.GetName().Version?.ToString(3)} starting" +
                 $"{(options.Simulate ? " (simulated LED device)" : "")}{(options.Tray ? " in the tray" : "")}");

        ThemeMode = options.Theme switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/RambiBox;component/Themes/Styles.xaml"),
        });

        Host = new AppHost(options, Dispatcher, logs);
        if (options.Page is not null) LastPage = options.Page;
        Host.Start();

        try { _system = new SystemIntegration(Host); }
        catch (Exception ex) { Log.Error("Windows integration failed", ex); }

        try { _tray = new TrayIconService(this, Host); }
        catch (Exception ex) { Log.Error("Tray icon failed", ex); }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + suffix);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName + suffix);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => Dispatcher.BeginInvoke(ExitApp), null, Timeout.Infinite, executeOnlyOnce: true);

        bool showWindow = !options.Tray || Host.FirstRun || _tray is null;
        if (showWindow)
        {
            ShowMainWindow();
        }
        else
        {
            // Started silently: if AmbiBox still holds the LED controller, say so once via a notification.
            void Once()
            {
                Host.AmbiBoxStateChanged -= Once;
                if (Host.AmbiBoxRunning && _main is null)
                    _tray?.Notify("AmbiBox is still running", "AmbiBox is using your LED controller. Click to switch to RambiBox.");
            }
            Host.AmbiBoxStateChanged += Once;
        }
    }

    public void ShowMainWindow()
    {
        if (_exiting) return;
        try
        {
            if (_main is null)
            {
                _main = new MainWindow(Host, LastPage);
                _main.Closed += (_, _) =>
                {
                    _main = null;
                    // nothing UI related keeps running once the window is gone
                    Host.SetOverride(UiOverride.None);
                };
            }
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the main window", ex);
        }
    }

    /// <summary>Opens the full-screen phone calibration on the captured monitor.</summary>
    public void StartCalibration()
    {
        if (_exiting) return;
        if (_calibration is not null)
        {
            _calibration.Activate();
            return;
        }
        PhoneCalibrationService? service = null;
        try
        {
            service = new PhoneCalibrationService(Host.Engine,
                () => Host.CurrentWhiteBalance, Host.ApplyWhiteBalance,
                Host.ApplyChannelGamma, () => Host.CurrentChannelGamma);
        }
        catch (Exception ex)
        {
            Log.Error("Phone calibration is not available", ex);
        }
        _calibration = new CalibrationWindow(Host, service);
        _calibration.Closed += (_, _) => _calibration = null;
        _calibration.Show();
        _calibration.Activate();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exit requested");
        try { _calibration?.Cancel(); } catch (Exception ex) { Log.Warn($"Closing calibration failed: {ex.Message}"); }
        try { _main?.Close(); } catch { /* ignore */ }
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Host?.ShutdownLeds();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Host is not null)
        {
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            _exitWait?.Unregister(null);
            _exitEvent?.Dispose();
            _tray?.Dispose();
            _system?.Dispose();
            Host.Dispose(); // stops the engine and turns the LEDs off
            Log.Info("RambiBox exited");
            Log.Flush();
        }
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    // ------------------------------------------------------------------ global error handling

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        var now = DateTime.UtcNow;
        if (now - _uiErrorWindow > TimeSpan.FromSeconds(10))
        {
            _uiErrorWindow = now;
            _uiErrors = 0;
        }
        // Keep running; but if the UI keeps failing in a loop, let it crash rather than spin.
        e.Handled = ++_uiErrors < 50;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error($"Unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject as Exception);
        Log.Flush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
