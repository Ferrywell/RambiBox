using System.Windows.Threading;
using RambiBox.App.Dev;
using RambiBox.App.Services;
using RambiBox.Core;
using RambiBox.Core.Api;
using RambiBox.Core.Calibration;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Engine;

namespace RambiBox.App;

/// <summary>White balance, per-channel gamma and LED color matrix at one moment (to undo a calibration).</summary>
public sealed record CalibrationSnapshot((double R, double G, double B) WhiteBalance, (double R, double G, double B) ChannelGamma,
    double[]? ColorMatrix = null)
{
    /// <summary>Value equality including the matrix contents.</summary>
    public bool SameValues(CalibrationSnapshot o)
    {
        static bool Near((double R, double G, double B) a, (double R, double G, double B) b) =>
            Math.Abs(a.R - b.R) < 1e-9 && Math.Abs(a.G - b.G) < 1e-9 && Math.Abs(a.B - b.B) < 1e-9;
        if (!Near(WhiteBalance, o.WhiteBalance) || !Near(ChannelGamma, o.ChannelGamma)) return false;
        if (ColorMatrix is null || o.ColorMatrix is null) return ColorMatrix is null && o.ColorMatrix is null;
        for (int i = 0; i < 9; i++) if (Math.Abs(ColorMatrix[i] - o.ColorMatrix[i]) > 1e-9) return false;
        return true;
    }
}

/// <summary>Result of the background device detection, shown as an InfoBar (and a tray notification).</summary>
/// <param name="Action">Switched automatically, suggestion, or several to choose from.</param>
/// <param name="Device">The device switched to / suggested (null for <see cref="AutoSwitchAction.Choose"/>).</param>
/// <param name="Count">Number of controllers found.</param>
public sealed record AutoDetectNotice(AutoSwitchAction Action, DiscoveredDevice? Device, int Count);

/// <summary>Command line options.</summary>
/// <param name="Demo"><c>--demo</c>: simulated LED device plus a synthetic movie scene instead of the real desktop,
/// with fresh default settings in a temporary folder (screenshots, demos). Implies <paramref name="Simulate"/>.</param>
public sealed record AppOptions(bool Tray, bool Simulate, string? Theme, string? Page, bool Exit = false, bool Demo = false)
{
    public static AppOptions Parse(string[] args)
    {
        bool tray = false, sim = false, exit = false, demo = false;
        string? theme = null, page = null;
        foreach (var a in args)
        {
            var arg = a.Trim().ToLowerInvariant();
            if (arg is "--tray" or "/tray") tray = true;
            else if (arg is "--simulate" or "/simulate") sim = true;
            else if (arg is "--exit" or "/exit") exit = true;
            else if (arg is "--demo" or "/demo") demo = sim = true;
            else if (arg.StartsWith("--theme=")) theme = arg["--theme=".Length..];
            else if (arg.StartsWith("--page=")) page = arg["--page=".Length..];
        }
        return new AppOptions(tray, sim, theme, page, exit, demo);
    }
}

/// <summary>
/// Owns the settings, the light engine and the device/sampler, and is the single place where settings
/// change: UI code mutates <see cref="Settings"/> on the UI thread and calls <see cref="Commit"/>, which
/// validates, pushes the new snapshot to the engine immediately and saves debounced on a background thread.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Timer _saveTimer;
    private AppSettings? _pendingSave;
    private bool _displayOff, _locked, _ledsShutDown, _lastStartWithWindows;
    private int _disposed;
    private readonly bool _firstRunWithoutImport;
    private CancellationTokenSource? _discoveryCts;

    public AppOptions Options { get; }
    public SettingsStore Store { get; }
    /// <summary>The live settings. Only touch on the UI thread; call <see cref="Commit"/> after changing.</summary>
    public AppSettings Settings { get; }
    public IScreenSampler Sampler { get; }
    public ILedDevice Device { get; }
    public LightEngine Engine { get; }
    public LogBuffer Logs { get; }

    /// <summary>Summary of the first-run AmbiBox import (null when nothing was imported).</summary>
    public string? ImportSummary { get; set; }
    public bool FirstRun { get; }

    /// <summary>White balance + channel gamma + color matrix from before the last calibration (for "Restore previous").</summary>
    public CalibrationSnapshot? PreviousCalibration { get; set; }

    public UiOverride ActiveOverride { get; private set; }
    public bool CalibrationActive { get; private set; }

    public bool AmbiBoxRunning { get; private set; }
    public bool AmbiBoxInAutostart { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="Settings"/> changed. Argument = the object that made the change.</summary>
    public event Action<object?>? SettingsChanged;
    /// <summary>Raised on the UI thread when serial devices were added/removed (WM_DEVICECHANGE, debounced).</summary>
    public event Action? DevicesChanged;
    /// <summary>Raised on the UI thread when the device status or a pause reason changed.</summary>
    public event Action? DeviceStatusChanged;
    public event Action? OverrideChanged;
    public event Action? AmbiBoxStateChanged;
    /// <summary>Raised on the UI thread when the Prismatik API server's state or its control of the LEDs changed.</summary>
    public event Action? ApiStatusChanged;

    /// <summary>Prismatik-compatible API server for integrations (Home Assistant, scripts, …).</summary>
    public PrismatikApiServer Api { get; }
    private readonly PrismatikHostAdapter _apiHost;
    private bool _started, _lastEnabled;
    private LightMode _lastMode;

    /// <summary>Latest API control state (UI thread).</summary>
    public PrismatikControl ApiControl { get; private set; } = PrismatikControl.None;

    /// <summary>An API client's colors are on the LEDs right now (not hidden by a test pattern, calibration or "lights off").</summary>
    public bool ApiControlling => ApiControl.Active && Settings.Enabled && ActiveOverride == UiOverride.None && !CalibrationActive;

    public AppHost(AppOptions options, Dispatcher dispatcher, LogBuffer logs)
    {
        Options = options;
        Logs = logs;
        _dispatcher = dispatcher;
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);

        // A simulated run keeps its own settings file so it never disturbs the real configuration;
        // a demo run always starts from fresh defaults in a temporary folder.
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RambiBox");
        Store = new SettingsStore(options.Demo ? Path.Combine(DemoDirectory, "settings.json")
            : options.Simulate ? Path.Combine(dir, "settings.simulate.json") : null);
        if (options.Demo)
        {
            try { File.Delete(Store.FilePath); File.Delete(Store.FilePath + ".bak"); }
            catch (Exception ex) { Log.Warn($"Could not reset the demo settings: {ex.Message}"); }
        }

        AppSettings? loaded = Store.Exists ? Store.Load() : null;
        FirstRun = loaded is null || !loaded.General.FirstRunDone;
        if (FirstRun)
        {
            string summary = "";
            var imported = options.Demo ? null : AmbiBoxImporter.TryImport(out summary);
            if (imported is not null)
            {
                Settings = imported;
                ImportSummary = summary;
            }
            else
            {
                Settings = loaded ?? new AppSettings();
                Log.Info(options.Demo ? "Demo: using default settings" : "No AmbiBox settings found; using defaults");
            }
            if (options.Demo) Settings.Static.Color = 0xFF9F45; // warm amber looks nicer than the default green
            Settings.General.FirstRunDone = true;
        }
        else
        {
            Settings = loaded!;
        }
        if (Settings.Layout.Zones.Count == 0)
            Settings.Layout.Zones = LayoutGenerator.Generate(Settings.Layout);
        SettingsSerializer.Normalize(Settings);
        if (FirstRun) TrySave(Settings.Clone());
        _firstRunWithoutImport = FirstRun && ImportSummary is null;

        // Windows Graphics Capture by default (Desktop Duplication can make Chromium stutter), switchable live.
        Sampler = options.Demo ? new DemoScreenSampler() : new SwitchingScreenSampler();
        Device = options.Simulate ? new SimulatedLedDevice(demo: options.Demo) : new SwitchingLedDevice();
        Device.DeviceFound += OnDeviceFound;
        Device.StatusChanged += OnDeviceStatusChanged;
        Engine = new LightEngine(Sampler, Device);

        _lastStartWithWindows = Settings.General.StartWithWindows;
        SnapshotWhiteBalance();

        _apiHost = new PrismatikHostAdapter(this, dispatcher);
        _apiHost.Refresh(Settings);
        _lastEnabled = Settings.Enabled;
        _lastMode = Settings.Mode;
        Api = new PrismatikApiServer(_apiHost, AppInfo.Version);
        Api.StatusChanged += _ => _dispatcher.BeginInvoke(() => ApiStatusChanged?.Invoke());
    }

    /// <summary>Settings and log folder of a <c>--demo</c> run (temporary, recreated each start).</summary>
    public static string DemoDirectory => Path.Combine(Path.GetTempPath(), "RambiBox.Demo");

    public void Start()
    {
        Engine.Start(Settings);
        _started = true;
        Api.Apply(Settings.Api);
        SyncStartup();
        _ = RefreshAmbiBoxAsync();
        if (!Options.Demo) _ = AutoDetectAsync(); // the demo shows a clean, already connected setup
    }

    // ------------------------------------------------------------------ settings

    /// <summary>
    /// Validates the live settings, pushes them to the engine (immediately) and schedules a save (~500 ms debounce).
    /// UI thread only.
    /// </summary>
    public void Commit(object? sender = null, bool pushToEngine = true)
    {
        SettingsSerializer.Normalize(Settings);
        if (pushToEngine) Engine.UpdateSettings(Settings);
        Volatile.Write(ref _pendingSave, Settings.Clone());
        _saveTimer.Change(500, Timeout.Infinite);
        ApplyPauses();
        SnapshotWhiteBalance();
        if (_lastStartWithWindows != Settings.General.StartWithWindows)
        {
            _lastStartWithWindows = Settings.General.StartWithWindows;
            SyncStartup();
        }
        SyncApi();
        SettingsChanged?.Invoke(sender);
    }

    /// <summary>Replaces all settings (reset / import) and commits.</summary>
    public void ReplaceSettings(AppSettings s, object? sender = null)
    {
        Settings.Enabled = s.Enabled;
        Settings.Mode = s.Mode;
        Settings.Device = s.Device;
        Settings.Layout = s.Layout;
        Settings.Color = s.Color;
        Settings.Capture = s.Capture;
        Settings.Static = s.Static;
        Settings.General = s.General;
        Settings.Api = s.Api;
        Settings.General.FirstRunDone = true;
        if (Settings.Layout.Zones.Count == 0) Settings.Layout.Zones = LayoutGenerator.Generate(Settings.Layout);
        Commit(sender);
    }

    private readonly object _saveLock = new();

    private void SaveNow()
    {
        // Take the snapshot inside the lock so an older snapshot can never be written after a newer one
        // (timer callback vs. FlushSave at exit).
        lock (_saveLock)
        {
            var s = Interlocked.Exchange(ref _pendingSave, null);
            if (s is not null) TrySave(s);
        }
    }

    private void TrySave(AppSettings s)
    {
        try { Store.Save(s); }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }

    /// <summary>Writes pending changes now (shutdown).</summary>
    public void FlushSave()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveNow();
    }

    private void SyncStartup()
    {
        if (Options.Simulate)
        {
            Log.Info($"(simulate) Start with Windows = {Settings.General.StartWithWindows}; registry left untouched");
            return;
        }
        Volatile.Write(ref _startupWanted, Settings.General.StartWithWindows ? 1 : 0);
        if (Interlocked.Exchange(ref _startupSyncQueued, 1) == 1) return; // a sync is already queued; it reads the latest value
        Task.Run(() =>
        {
            lock (_startupSyncLock)
            {
                Volatile.Write(ref _startupSyncQueued, 0);
                StartupRegistration.Sync(Volatile.Read(ref _startupWanted) == 1);
            }
        });
    }

    private readonly object _startupSyncLock = new();
    private int _startupWanted, _startupSyncQueued;

    /// <summary>Called by the calibration (any thread): apply + persist a new white balance.</summary>
    public void ApplyWhiteBalance(double r, double g, double b)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Settings.Color.WhiteR = r;
            Settings.Color.WhiteG = g;
            Settings.Color.WhiteB = b;
            Commit(this);
        });
    }

    /// <summary>Called by the calibration (any thread): apply + persist a new per-channel gamma.</summary>
    public void ApplyChannelGamma(double r, double g, double b)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Settings.Color.ChannelGammaR = r;
            Settings.Color.ChannelGammaG = g;
            Settings.Color.ChannelGammaB = b;
            Commit(this);
        });
    }

    /// <summary>Called by the calibration (any thread): apply + persist the LED color matrix (null = off).</summary>
    public void ApplyColorMatrix(double[]? matrix)
    {
        var m = matrix is null ? null : (double[])matrix.Clone();
        _dispatcher.BeginInvoke(() =>
        {
            Settings.Color.ColorMatrix = m;
            Commit(this);
        });
    }

    /// <summary>Applies a whole calibration snapshot (restore previous / cancel). Any thread.</summary>
    public void ApplyCalibration(CalibrationSnapshot s)
    {
        ApplyWhiteBalance(s.WhiteBalance.R, s.WhiteBalance.G, s.WhiteBalance.B);
        ApplyChannelGamma(s.ChannelGamma.R, s.ChannelGamma.G, s.ChannelGamma.B);
        ApplyColorMatrix(s.ColorMatrix);
    }

    /// <summary>Thread-safe snapshot of the per-channel gamma in effect.</summary>
    public (double R, double G, double B) CurrentChannelGamma
    {
        get { lock (_wbLock) return _gamma; }
    }
    private (double R, double G, double B) _gamma = (1, 1, 1);
    private double[]? _matrix;
    private double _brightness = 1;

    public CalibrationSnapshot CurrentCalibration
    {
        get { lock (_wbLock) return new CalibrationSnapshot(_wb, _gamma, _matrix is null ? null : (double[])_matrix.Clone()); }
    }

    /// <summary>The phone calibration's view of the settings (thread-safe; applies through the UI thread).</summary>
    public ICalibrationTarget CalibrationTarget => _calibrationTarget ??= new HostCalibrationTarget(this);
    private ICalibrationTarget? _calibrationTarget;

    /// <summary>The PC's local certificate authority for the phone's HTTPS calibration page.</summary>
    public LocalCertificateAuthority PhoneCa { get; } = new();

    private sealed class HostCalibrationTarget(AppHost host) : ICalibrationTarget
    {
        public CalibrationValues Read()
        {
            lock (host._wbLock)
                return new CalibrationValues(host._wb, host._gamma, host._matrix is null ? null : (double[])host._matrix.Clone(), host._brightness);
        }

        public void ApplyWhiteBalance(double r, double g, double b) => host.ApplyWhiteBalance(r, g, b);
        public void ApplyChannelGamma(double r, double g, double b) => host.ApplyChannelGamma(r, g, b);
        public void ApplyColorMatrix(double[]? matrix) => host.ApplyColorMatrix(matrix);
    }

    /// <summary>Thread-safe snapshot of the white balance in effect (updated on every commit).</summary>
    public (double R, double G, double B) CurrentWhiteBalance
    {
        get { lock (_wbLock) return _wb; }
    }
    private readonly object _wbLock = new();
    private (double R, double G, double B) _wb;

    private void SnapshotWhiteBalance()
    {
        lock (_wbLock)
        {
            _wb = (Settings.Color.WhiteR, Settings.Color.WhiteG, Settings.Color.WhiteB);
            _gamma = (Settings.Color.ChannelGammaR, Settings.Color.ChannelGammaG, Settings.Color.ChannelGammaB);
            _matrix = Settings.Color.ColorMatrix is { } m ? (double[])m.Clone() : null;
            _brightness = Settings.Color.Brightness;
        }
    }

    // ------------------------------------------------------------------ overrides

    /// <summary>Shows a UI test pattern on the LEDs (None restores normal output). Ignored while calibrating.</summary>
    public void SetOverride(UiOverride kind)
    {
        if (CalibrationActive && kind != UiOverride.None) return;
        if (kind == ActiveOverride) return;
        ActiveOverride = kind;
        _uiOverride = kind switch
        {
            UiOverride.White => new WhiteOverride(),
            UiOverride.Identify => new IdentifyOverride(),
            UiOverride.TestColors => new TestColorsOverride(),
            _ => null,
        };
        ApplyEngineOverride();
        OverrideChanged?.Invoke();
    }

    private ILedOverride? _uiOverride;

    public void SetCalibrationActive(bool active)
    {
        if (active) SetOverride(UiOverride.None);
        CalibrationActive = active;
        if (!active) ApplyEngineOverride(); // an API client may still be driving the LEDs
        OverrideChanged?.Invoke();
    }

    /// <summary>
    /// Decides what drives the LEDs instead of the normal output. Priority: calibration (sets the engine override
    /// itself) → UI test pattern → Prismatik API colors (only while the lights are on). UI thread.
    /// </summary>
    private void ApplyEngineOverride()
    {
        if (CalibrationActive) return;
        var o = _uiOverride ?? (ApiControl.Active && Settings.Enabled ? ApiControl.Override : null);
        if (!ReferenceEquals(Engine.Override, o)) Engine.SetOverride(o);
    }

    /// <summary>The API server took or released the LEDs (UI thread, via <see cref="PrismatikHostAdapter"/>).</summary>
    internal void OnApiControlChanged(PrismatikControl control)
    {
        ApiControl = control;
        ApplyEngineOverride();
        ApiStatusChanged?.Invoke();
    }

    /// <summary>
    /// Commit hook: refreshes the API's settings snapshot, starts/stops/restarts the server, and ends Prismatik's
    /// "persist on unlock" when the lights or the mode are switched (like Prismatik).
    /// </summary>
    private void SyncApi()
    {
        _apiHost.Refresh(Settings);
        if (Settings.Enabled != _lastEnabled || Settings.Mode != _lastMode)
        {
            _lastEnabled = Settings.Enabled;
            _lastMode = Settings.Mode;
            Api.ReleasePersistedColors();
        }
        if (_started) Api.Apply(Settings.Api);
        ApplyEngineOverride();
    }

    // ------------------------------------------------------------------ device / system events

    private void OnDeviceFound(SerialPortInfo info)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var d = Settings.Device;
            if (d.LastPort == info.PortName && d.LastUsbId == info.UsbId) return;
            d.LastPort = info.PortName;
            d.LastUsbId = info.UsbId;
            Log.Info($"LED controller found on {info.PortName} ({info.Description}, {info.UsbId ?? "no USB id"})");
            Commit(this, pushToEngine: false); // remembered for next start; the engine doesn't need it
        });
    }

    private void OnDeviceStatusChanged()
    {
        var status = Device.Status;
        _dispatcher.BeginInvoke(() =>
        {
            DeviceStatusChanged?.Invoke();
            if (status == DeviceStatus.PortBusy || (status == DeviceStatus.Connected && AmbiBoxRunning))
                _ = RefreshAmbiBoxAsync();
        });
    }

    /// <summary>WM_DEVICECHANGE (debounced, UI thread).</summary>
    public void OnDevicesChanged()
    {
        try { Device.Rescan(); } catch (Exception ex) { Log.Warn($"Rescan failed: {ex.Message}"); }
        DevicesChanged?.Invoke();
    }

    public void OnDisplayState(int state)
    {
        // 0 = off, 1 = on, 2 = dimmed (keep the lights on while dimmed)
        _displayOff = state == 0;
        ApplyPauses();
        DeviceStatusChanged?.Invoke();
    }

    public void OnLockChanged(bool locked)
    {
        _locked = locked;
        ApplyPauses();
        DeviceStatusChanged?.Invoke();
    }

    public void OnSuspend(bool suspended)
    {
        Engine.SetPaused(PauseReason.Suspended, suspended);
        if (suspended)
        {
            // Don't rely on the engine thread getting a turn before the system sleeps (it may be blocked in a
            // capture wait): send black and close the port synchronously, so boards that keep USB standby power
            // don't stay lit all night.
            try { Device.Blackout(); } catch (Exception ex) { Log.Warn($"Blackout before sleep failed: {ex.Message}"); }
        }
        else
        {
            // Blackout() leaves the device stopped until it is configured again.
            try { Device.Configure(Settings.Device, Settings.Layout.Zones.Count + Settings.Layout.Offset); }
            catch (Exception ex) { Log.Warn($"Restarting the LED device after resume failed: {ex.Message}"); }
            try { Device.Rescan(); } catch { /* ignore */ }
        }
        _dispatcher.BeginInvoke(() => DeviceStatusChanged?.Invoke());
    }

    private void ApplyPauses()
    {
        Engine.SetPaused(PauseReason.DisplayOff, _displayOff && Settings.General.OffWhenDisplayOff);
        Engine.SetPaused(PauseReason.Locked, _locked && Settings.General.OffWhenLocked);
    }

    /// <summary>Stops the engine and turns the LEDs off (Windows is shutting down). Safe to call more than once.</summary>
    public void ShutdownLeds()
    {
        lock (this)
        {
            if (_ledsShutDown) return;
            _ledsShutDown = true;
        }
        FlushSave();
        try { Api.Stop(); } catch (Exception ex) { Log.Warn($"API server stop failed: {ex.Message}"); }
        try { Engine.Stop(); } catch (Exception ex) { Log.Warn($"Engine stop failed: {ex.Message}"); }
        Log.Info("LEDs turned off (session ending)");
        Log.Flush(500);
    }

    // ------------------------------------------------------------------ device discovery

    /// <summary>How long one "Find devices" scan may take.</summary>
    public static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Controllers found by the last scan (UI thread).</summary>
    public IReadOnlyList<DiscoveredDevice> DiscoveredDevices { get; private set; } = [];
    /// <summary>True once a scan has completed in this session (to tell "nothing found" from "not searched yet").</summary>
    public bool HasDiscovered { get; private set; }
    public bool Discovering => _discoveryCts is not null;
    /// <summary>Raised on the UI thread when a scan starts or finishes.</summary>
    public event Action? DiscoveryChanged;

    /// <summary>What the background detection found (null = nothing to say).</summary>
    public AutoDetectNotice? AutoDetectNotice { get; private set; }
    public event Action? AutoDetectNoticeChanged;

    private Task<IReadOnlyList<DiscoveredDevice>>? _discoveryTask;

    /// <summary>
    /// Runs every discovery service (USB serial, HID, network) for up to <see cref="DiscoveryTimeout"/> and stores the
    /// results in <see cref="DiscoveredDevices"/>. UI thread. A scan already running is joined instead of restarted.
    /// In <c>--simulate</c> nothing real is touched (<see cref="SimulatedDiscovery"/>).
    /// </summary>
    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync()
    {
        if (_discoveryTask is not null && !_discoveryTask.IsCompleted) return _discoveryTask;
        _discoveryTask = DiscoverCoreAsync();
        return _discoveryTask;
    }

    private async Task<IReadOnlyList<DiscoveredDevice>> DiscoverCoreAsync()
    {
        var cts = new CancellationTokenSource();
        _discoveryCts = cts;
        DiscoveryChanged?.Invoke();
        try
        {
            Log.Info("Looking for LED controllers…");
            var found = Options.Simulate
                ? await SimulatedDiscovery.DiscoverAsync(DiscoveryTimeout, cts.Token)
                : await LedDeviceFactory.DiscoverAllAsync(DiscoveryTimeout, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            // the same controller can be reported twice (e.g. by two services or network interfaces)
            var unique = found.GroupBy(d => (d.Type, Id: d.Id.ToUpperInvariant())).Select(g => g.First()).ToList();
            Log.Info(unique.Count == 0 ? "No LED controllers found"
                : $"Found {unique.Count} LED controller(s): {string.Join(", ", unique.Select(d => $"{d.DisplayName} [{d.Type}]"))}");
            DiscoveredDevices = unique;
            HasDiscovered = true;
            return unique;
        }
        catch (OperationCanceledException)
        {
            Log.Info("Device search cancelled");
            return DiscoveredDevices;
        }
        catch (Exception ex)
        {
            Log.Warn($"Device search failed: {ex.Message}");
            return DiscoveredDevices;
        }
        finally
        {
            _discoveryCts = null;
            cts.Dispose();
            DiscoveryChanged?.Invoke();
        }
    }

    /// <summary>Stops a running scan (keeps the previous results).</summary>
    public void CancelDiscovery()
    {
        try { _discoveryCts?.Cancel(); }
        catch (ObjectDisposedException) { /* finished meanwhile */ }
    }

    /// <summary>Switches to a controller from <see cref="DiscoveredDevices"/> (type, port/host, id) and commits. UI thread.</summary>
    public void UseDevice(DiscoveredDevice device)
    {
        var d = Settings.Device;
        var oldType = d.Type;
        device = DeviceCatalog.ForCurrentProtocol(device, d); // a WLED found while DDP / E1.31 is selected keeps that protocol
        device.ApplyTo(d); // an unverified serial port keeps the user's serial protocol
        d.Name = device.DisplayName;
        d.BaudRate = DeviceCatalog.BaudAfterTypeChange(oldType, d.Type, d.BaudRate);
        Log.Info($"Using {device.DisplayName} ({DeviceCatalog.Get(d.Type).Name})");
        SetAutoDetectNotice(null);
        Commit(this);
    }

    public void DismissAutoDetectNotice() => SetAutoDetectNotice(null);

    private void SetAutoDetectNotice(AutoDetectNotice? notice)
    {
        if (notice == AutoDetectNotice) return;
        AutoDetectNotice = notice;
        AutoDetectNoticeChanged?.Invoke();
    }

    /// <summary>
    /// Once per start: when the configured controller hasn't connected after ~10 s, scan for others. Switches
    /// automatically only when nothing was set up yet and exactly one controller answers (see
    /// <see cref="DeviceAutoSwitchPolicy"/>); otherwise it only tells the user. Adalight auto-detect keeps running.
    /// </summary>
    private async Task AutoDetectAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (Volatile.Read(ref _disposed) != 0 || Device.Status == DeviceStatus.Connected) return;
            Log.Info("The LED controller hasn't connected yet; looking for other controllers");
            var found = await DiscoverAsync();
            if (Volatile.Read(ref _disposed) != 0 || Device.Status == DeviceStatus.Connected) return; // it came up meanwhile
            var decision = DeviceAutoSwitchPolicy.Decide(Settings.Device, _firstRunWithoutImport, found,
                LedDeviceFactory.IsSupported);
            switch (decision.Action)
            {
                case AutoSwitchAction.None:
                    return;
                case AutoSwitchAction.Switch:
                    Log.Info($"Switching automatically to {decision.Device!.DisplayName}");
                    UseDevice(decision.Device);
                    SetAutoDetectNotice(new AutoDetectNotice(AutoSwitchAction.Switch, decision.Device, 1));
                    break;
                default:
                    SetAutoDetectNotice(new AutoDetectNotice(decision.Action, decision.Device, decision.Candidates.Count));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Automatic device detection failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ AmbiBox hand-over

    public async Task RefreshAmbiBoxAsync()
    {
        if (Options.Demo) return; // the demo shows a clean, AmbiBox-free setup
        try
        {
            var (running, autostart) = await Task.Run(() => (AmbiBoxImporter.IsRunning(), AmbiBoxImporter.IsInAutostart()));
            if (running == AmbiBoxRunning && autostart == AmbiBoxInAutostart) return;
            AmbiBoxRunning = running;
            AmbiBoxInAutostart = autostart;
            AmbiBoxStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"AmbiBox check failed: {ex.Message}");
        }
    }

    /// <summary>Closes AmbiBox so the COM port becomes free, then rescans.</summary>
    public async Task<bool> CloseAmbiBoxAsync()
    {
        bool ok = await Task.Run(AmbiBoxImporter.Stop);
        try { Device.Rescan(); } catch { /* ignore */ }
        await RefreshAmbiBoxAsync();
        return ok;
    }

    public async Task<bool> DisableAmbiBoxAutostartAsync()
    {
        bool ok = await Task.Run(AmbiBoxImporter.DisableAutostart);
        await RefreshAmbiBoxAsync();
        return ok;
    }

    public async Task<bool> RestoreAmbiBoxAutostartAsync()
    {
        bool ok = await Task.Run(AmbiBoxImporter.RestoreAutostart);
        await RefreshAmbiBoxAsync();
        return ok;
    }

    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelDiscovery();
        FlushSave();
        _saveTimer.Dispose();
        Device.DeviceFound -= OnDeviceFound;
        Device.StatusChanged -= OnDeviceStatusChanged;
        try { Api.Dispose(); } catch (Exception ex) { Log.Warn($"API server stop failed: {ex.Message}"); }
        try { Engine.Dispose(); } catch (Exception ex) { Log.Warn($"Engine dispose failed: {ex.Message}"); }
        Logs.Dispose();
    }
}
