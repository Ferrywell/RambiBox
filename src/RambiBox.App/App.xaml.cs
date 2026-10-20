using System.Windows;
using System.Windows.Threading;
using RambiBox.App.Services;
using RambiBox.App.Views;
using RambiBox.Core;
using RambiBox.Core.Calibration;
using RambiBox.Core.Devices;
using RambiBox.Core.Updates;

namespace RambiBox.App;

/// <summary>
/// Composition root: single instance, settings, engine, tray icon and Windows integration.
/// The main window is created on demand and fully released when closed (the app then idles in the tray).
/// </summary>
public partial class App : Application, IUpdateShell
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
    private UpdateService? _updates;
    private AppOptions? _options;
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
        try
        {
            StartupCore(e);
        }
        catch (Exception ex)
        {
            // Never live on as a zombie (no tray icon, single-instance mutex held): log, tell the user, exit.
            Log.Error("Startup failed", ex);
            Log.Flush();
            try { Host?.Engine.Dispose(); } catch { }
            if (_options?.UpdateToken is not null)
            {
                // started by the updater: exit at once so the previous version is restored (it explains what happened)
                Shutdown(1);
                return;
            }
            MessageBox.Show($"RambiBox could not start:\n\n{ex.Message}\n\nDetails are in {Log.FilePath}",
                "RambiBox", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartupCore(StartupEventArgs e)
    {
        var options = AppOptions.Parse(e.Args);
        _options = options;
        // A simulated (development) or demo instance may run next to the real one.
        string suffix = options.Demo ? ".Demo" : options.Simulate ? ".Simulate" : "";
        if (options.Demo)
        {
            // Before anything logs: keep the demo's log out of the real one (and its paths out of screenshots).
            Environment.SetEnvironmentVariable("RAMBIBOX_LOG_DIR", Path.Combine(AppHost.DemoDirectory, "logs"));
        }

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
        if (!createdNew && options.UpdatedFrom is not null)
        {
            // Started by the updater of the previous version: it releases the mutex before starting us, but give it
            // time instead of treating it as "already running".
            try { createdNew = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { createdNew = true; } // the old process ended without releasing it: ours now
            if (!createdNew) Log.Warn($"Updated from {options.UpdatedFrom}, but the previous version still holds the single-instance lock");
        }
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

        Log.Info($"RambiBox {AppInfo.Version} starting" +
                 $"{(options.Demo ? " (demo: synthetic picture, simulated LED device)" : options.Simulate ? " (simulated LED device)" : "")}{(options.Tray ? " in the tray" : "")}" +
                 $"{(options.UpdatedFrom is { } from ? $" (updated from {from})" : "")}{(options.UpdateFailed is { } failed ? $" (the update to {failed} failed and was undone)" : "")}");

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
        Host.AutoDetectNoticeChanged += OnAutoDetectNotice;

        _updates = new UpdateService(Host, this);
        Host.Updates = _updates;
        _updates.Start();
        if (!options.Demo) CleanUpAfterUpdate(options);

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
            MemoryTrim.Schedule(TimeSpan.FromSeconds(15)); // after startup work (JIT, first captures) has settled

            // started in the tray after an (automatic) update or a failed one: say so once
            if (options.UpdatedFrom is not null)
            {
                LastPage = "home";
                _tray?.Notify($"RambiBox was updated to {AppInfo.Version}", "Click to see what's new.");
            }
            else if (options.UpdateFailed is { } failedVersion)
            {
                LastPage = "home";
                _tray?.Notify("Update failed", $"RambiBox {failedVersion} couldn't be installed or didn't start correctly, so you're still on {AppInfo.Version}. Details are in the log.");
            }
        }

        if (options.UpdateToken is { } token) SignalHealthyStartLater(token);
    }

    /// <summary>
    /// Started by the updater: tell the previous version that this one works (engine, tray and window are up and the
    /// UI thread is still responsive ~5 s later). Until then it may roll the update back.
    /// </summary>
    private void SignalHealthyStartLater(string token)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_exiting) return;
            bool ok = UpdateHandshake.Signal(token);
            Log.Info(ok ? "Update: reported a healthy start to the previous version" : "Update: the previous version is no longer waiting");
        };
        timer.Start();
    }

    /// <summary>
    /// After an update: the previous exe (RambiBox.exe.old) is deleted at a later start (not during the first one, when the
    /// old process may still be running from it), and downloads of this version or older are removed.
    /// </summary>
    private void CleanUpAfterUpdate(AppOptions options)
    {
        var exe = Environment.ProcessPath;
        var updates = Host.UpdatesDirectory;
        var current = AppInfo.SemVersion;
        bool deleteOld = options.UpdatedFrom is null && !options.Simulate && exe is not null;
        Task.Run(() =>
        {
            if (deleteOld)
            {
                var old = UpdateInstaller.OldPath(exe!);
                try
                {
                    if (File.Exists(old))
                    {
                        File.Delete(old);
                        Log.Info($"Deleted the previous version ({Path.GetFileName(old)})");
                    }
                }
                catch (Exception ex) { Log.Warn($"Could not delete {old} yet: {ex.Message}"); }
            }
            UpdateDownloader.CleanupWhere(updates, v => v > current);
        });
    }

    // ------------------------------------------------------------------ IUpdateShell

    bool IUpdateShell.WindowVisible => _main is { IsVisible: true } m && m.WindowState != WindowState.Minimized;

    void IUpdateShell.Notify(string title, string message, string page)
    {
        LastPage = page;
        _tray?.Notify(title, message);
    }

    void IUpdateShell.PrepareForInstall()
    {
        Log.Info("Preparing to install an update: stopping the lights and handing over the single-instance lock");
        try { _calibration?.Cancel(); } catch (Exception ex) { Log.Warn($"Closing calibration failed: {ex.Message}"); }
        Host.ShutdownLeds("installing an update"); // LEDs off, COM port free, settings saved
        _showWait?.Unregister(null);
        _showWait = null;
        _showEvent?.Dispose();
        _showEvent = null;
        _exitWait?.Unregister(null);
        _exitWait = null;
        _exitEvent?.Dispose();
        _exitEvent = null;
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (Exception ex) { Log.Warn($"Releasing the single-instance lock failed: {ex.Message}"); }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    void IUpdateShell.ExitForUpdate() => ExitApp();

    /// <summary>Opens the window on a page (e.g. the tray's "Check for updates…").</summary>
    public void ShowPage(string page)
    {
        LastPage = page;
        if (_main is not null) _main.ShowPage(page);
        ShowMainWindow();
    }

    /// <summary>
    /// Background device detection found something: the window shows an InfoBar; when the window isn't open, a tray
    /// notification says so (clicking it opens the window, on the Device page when there is a choice to make).
    /// </summary>
    private void OnAutoDetectNotice()
    {
        if (Host.AutoDetectNotice is not { } n || _tray is null || _main is { IsVisible: true }) return;
        switch (n.Action)
        {
            case AutoSwitchAction.Switch:
                _tray.Notify($"Found {n.Device!.DisplayName}", "RambiBox is using it for your LEDs now.");
                break;
            case AutoSwitchAction.Suggest:
                LastPage = "device";
                _tray.Notify($"Found {n.Device!.DisplayName}", "Your LED controller isn't responding. Click to use this one instead.");
                break;
            case AutoSwitchAction.Choose:
                LastPage = "device";
                _tray.Notify($"Found {n.Count} LED controllers", "Click to choose the one RambiBox should use.");
                break;
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
                    MemoryTrim.Schedule(TimeSpan.FromSeconds(3));
                };
            }
            MemoryTrim.Cancel();
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
            service = new PhoneCalibrationService(Host.Engine, Host.CalibrationTarget, Host.PhoneCa);
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
            _updates?.Dispose();
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
        if (e.IsTerminating)
        {
            // Best effort: don't leave the LEDs lit when the process dies.
            try { ((App)Current).Host?.Device.Blackout(); } catch { }
        }
        Log.Flush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
