using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RambiBox.App.Services;
using RambiBox.Core;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Engine;

namespace RambiBox.App.ViewModels;

public sealed record NavItem(string Key, string Glyph, string Title);
public sealed record Option<T>(T Value, string Display);

public enum StatusLevel { Good, Busy, Problem, Off }

/// <summary>
/// View model of the main window. Lives only while the window exists; the status refresh (2 Hz) runs only
/// while the window is visible.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statusTimer;
    private IReadOnlyList<SerialPortInfo> _ports = Array.Empty<SerialPortInfo>();
    private bool _viewActive;

    public MainViewModel(AppHost host)
    {
        _host = host;
        S = new SettingsViewModel(host);

        NavItems =
        [
            new("home", "", "Home"),
            new("colors", "", "Colors"),
            new("static", "", "Static & mood"),
            new("layout", "", "LED layout"),
            new("device", "", "Device"),
            new("calibration", "", "Calibration"),
            new("settings", "", "Settings"),
        ];
        _selectedNav = NavItems[0];

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RefreshStatus();

        _host.DeviceStatusChanged += OnDeviceStatusChanged;
        _host.DevicesChanged += OnDevicesChanged;
        _host.OverrideChanged += OnOverrideChanged;
        _host.AmbiBoxStateChanged += OnAmbiBoxStateChanged;
        _host.SettingsChanged += OnSettingsChanged;

        CloseAmbiBoxCommand = new RelayCommand(async () => await CloseAmbiBoxAsync(), () => !_closingAmbiBox);
        RemoveAmbiBoxAutostartCommand = new RelayCommand(async () =>
        {
            if (await _host.DisableAmbiBoxAutostartAsync()) AutostartJustRemoved = true;
        });
        UndoAutostartCommand = new RelayCommand(async () =>
        {
            AutostartJustRemoved = false;
            await _host.RestoreAmbiBoxAutostartAsync();
        });
        DismissImportCommand = new RelayCommand(() =>
        {
            _host.ImportSummary = null;
            OnPropertyChanged(nameof(ShowImportBar));
        });
        DismissAutostartBarCommand = new RelayCommand(() =>
        {
            _autostartBarDismissed = true;
            OnPropertyChanged(nameof(ShowAmbiBoxAutostartBar));
        });
        DismissAutostartRemovedCommand = new RelayCommand(() => AutostartJustRemoved = false);
        StartCalibrationCommand = new RelayCommand(() => App.Current.StartCalibration(), () => !_host.CalibrationActive);
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(Log.Directory));
        OpenSettingsFolderCommand = new RelayCommand(() => OpenFolder(Path.GetDirectoryName(_host.Store.FilePath)!));
        NavigateCommand = new RelayCommand(p =>
        {
            if (p is string key && NavItems.FirstOrDefault(n => n.Key == key) is { } item) SelectedNav = item;
        });
        RestorePreviousWhiteBalanceCommand = new RelayCommand(RestorePreviousWhiteBalance, () => _host.PreviousCalibration is not null);
        AllowFirewallCommand = new RelayCommand(async () => await AllowFirewallAsync(), () => !_firewallBusy);
        ResetPhoneCertCommand = new RelayCommand(async () => await ResetPhoneCertAsync(), () => !_host.CalibrationActive);

        RefreshPortsAsync();
        RefreshMonitors();
        RefreshStatus();
    }

    public SettingsViewModel S { get; }
    public AppHost Host => _host;
    public bool IsSimulated => _host.Options.Simulate;

    // ------------------------------------------------------------------ navigation

    public IReadOnlyList<NavItem> NavItems { get; }

    private NavItem _selectedNav;
    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !SetField(ref _selectedNav, value)) return;
            // test patterns belong to the page that started them
            _host.SetOverride(UiOverride.None);
            App.Current.LastPage = value.Key;
            NavigationChanged?.Invoke(value.Key);
        }
    }

    public event Action<string>? NavigationChanged;

    public ICommand NavigateCommand { get; }

    // ------------------------------------------------------------------ visibility (timers run only while visible)

    public bool IsViewActive
    {
        get => _viewActive;
        set
        {
            if (!SetField(ref _viewActive, value)) return;
            if (value)
            {
                RefreshStatus();
                _statusTimer.Start();
                _ = _host.RefreshAmbiBoxAsync();
            }
            else _statusTimer.Stop();
            ViewActiveChanged?.Invoke(value);
        }
    }

    public event Action<bool>? ViewActiveChanged;

    // ------------------------------------------------------------------ status

    public string StatusText { get; private set; } = "Starting…";
    public string StatusDetail { get; private set; } = "";
    public StatusLevel StatusLevel { get; private set; } = StatusLevel.Busy;
    public bool ShowCloseAmbiBoxInStatus { get; private set; }

    // device page
    public string DeviceTitle { get; private set; } = "";
    public string DeviceDescription { get; private set; } = "";
    public string LinkFpsText { get; private set; } = "–";
    public string OutputFpsText { get; private set; } = "–";
    public string CaptureText { get; private set; } = "–";
    public string MonitorText { get; private set; } = "–";

    private void RefreshStatus()
    {
        var st = _host.Engine.GetStats();
        var s = _host.Settings;
        string port = st.PortName ?? "the LED controller";
        string text, detail = "";
        StatusLevel level;
        bool closeAmbiBox = false;

        switch (st.DeviceStatus)
        {
            case DeviceStatus.Connected:
                if (st.OverrideActive)
                {
                    text = $"Showing a test pattern on {port}";
                    level = StatusLevel.Busy;
                }
                else if (!s.Enabled)
                {
                    text = "Lights are off";
                    detail = $"Connected on {port}";
                    level = StatusLevel.Off;
                }
                else if ((st.Paused & ~PauseReason.UserOff) != PauseReason.None)
                {
                    text = $"Paused – {DescribePause(st.Paused)}";
                    detail = $"Connected on {port}";
                    level = StatusLevel.Off;
                }
                else
                {
                    string fps = st.OutputFps >= 1.5 ? $"{st.OutputFps:0} fps" : "idle (nothing changing)";
                    text = $"Connected on {port} · {fps}";
                    if (s.Mode == LightMode.Ambilight && st.CaptureMs > 0) text += $" · capture {st.CaptureMs:0.0} ms";
                    level = StatusLevel.Good;
                }
                break;
            case DeviceStatus.PortBusy:
                if (_host.AmbiBoxRunning)
                {
                    text = $"{port} is in use by AmbiBox";
                    closeAmbiBox = true;
                }
                else text = $"{port} is in use by another program";
                detail = "RambiBox keeps retrying and connects as soon as the port is free.";
                level = StatusLevel.Problem;
                break;
            case DeviceStatus.Searching:
                text = "Looking for the LED controller…";
                detail = "Plug in the USB cable of your LED strip. RambiBox finds it automatically.";
                level = StatusLevel.Busy;
                break;
            default:
                text = "Starting…";
                level = StatusLevel.Busy;
                break;
        }

        SetStatus(text, detail, level, closeAmbiBox);

        // Device page details
        DeviceTitle = st.DeviceStatus switch
        {
            DeviceStatus.Connected => "Connected",
            DeviceStatus.PortBusy => "Port in use",
            DeviceStatus.Searching => "Searching…",
            _ => "Not started",
        };
        var info = _ports.FirstOrDefault(p => string.Equals(p.PortName, st.PortName, StringComparison.OrdinalIgnoreCase));
        DeviceDescription = info is not null ? $"{info.Description} ({info.PortName})"
            : st.PortName is not null ? st.PortName : "No LED controller found yet";
        LinkFpsText = st.LinkMaxFps > 0 ? $"{st.LinkMaxFps:0} fps" : "–";
        OutputFpsText = st.DeviceStatus == DeviceStatus.Connected ? $"{st.OutputFps:0} fps" : "–";
        CaptureText = st.CaptureMs > 0 && s.Mode == LightMode.Ambilight
            ? $"{st.CaptureFps:0} fps · {st.CaptureMs:0.0} ms" : "–";
        MonitorText = st.Monitor is { } m ? $"{m.FriendlyName}{(m.IsHdr ? " · HDR" : "")}" : "–";
        foreach (var n in new[] { nameof(DeviceTitle), nameof(DeviceDescription), nameof(LinkFpsText), nameof(OutputFpsText), nameof(CaptureText), nameof(MonitorText) })
            OnPropertyChanged(n);
    }

    private void SetStatus(string text, string detail, StatusLevel level, bool closeAmbiBox)
    {
        if (text != StatusText) { StatusText = text; OnPropertyChanged(nameof(StatusText)); }
        if (detail != StatusDetail) { StatusDetail = detail; OnPropertyChanged(nameof(StatusDetail)); }
        if (level != StatusLevel) { StatusLevel = level; OnPropertyChanged(nameof(StatusLevel)); }
        if (closeAmbiBox != ShowCloseAmbiBoxInStatus) { ShowCloseAmbiBoxInStatus = closeAmbiBox; OnPropertyChanged(nameof(ShowCloseAmbiBoxInStatus)); }
    }

    private static string DescribePause(PauseReason p)
    {
        var parts = new List<string>();
        if (p.HasFlag(PauseReason.DisplayOff)) parts.Add("the display is off");
        if (p.HasFlag(PauseReason.Locked)) parts.Add("the PC is locked");
        if (p.HasFlag(PauseReason.Suspended)) parts.Add("the PC is asleep");
        return string.Join(", ", parts);
    }

    private void OnDeviceStatusChanged()
    {
        RefreshStatus();
        // the engine publishes its stats a moment later
        if (!_viewActive) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (_, _) => { t.Stop(); RefreshStatus(); };
        t.Start();
    }

    private void OnSettingsChanged(object? sender)
    {
        OnPropertyChanged(nameof(ChannelGammaText));
        OnPropertyChanged(nameof(ColorMatrixText));
        if (_viewActive) RefreshStatus();
    }

    // ------------------------------------------------------------------ InfoBars (AmbiBox hand-over, import)

    public bool ShowAmbiBoxRunningBar => _host.AmbiBoxRunning;
    public bool AmbiBoxInAutostart => _host.AmbiBoxInAutostart;
    private bool _autostartBarDismissed;
    public bool ShowAmbiBoxAutostartBar => !_host.AmbiBoxRunning && _host.AmbiBoxInAutostart && !_autostartBarDismissed;

    private bool _autostartJustRemoved;
    public bool AutostartJustRemoved
    {
        get => _autostartJustRemoved;
        set => SetField(ref _autostartJustRemoved, value);
    }

    public void ShowImportSummary(string summary)
    {
        _host.ImportSummary = summary;
        OnPropertyChanged(nameof(ImportSummary));
        OnPropertyChanged(nameof(ShowImportBar));
    }

    public bool ShowImportBar => !string.IsNullOrEmpty(_host.ImportSummary);
    public string ImportSummary => _host.ImportSummary ?? "";

    private bool _closingAmbiBox;
    public bool ClosingAmbiBox
    {
        get => _closingAmbiBox;
        private set
        {
            if (SetField(ref _closingAmbiBox, value)) OnPropertyChanged(nameof(CloseAmbiBoxLabel));
        }
    }

    public string CloseAmbiBoxLabel => _closingAmbiBox ? "Closing AmbiBox…" : "Close AmbiBox and switch";

    private string? _ambiBoxError;
    public string? AmbiBoxError
    {
        get => _ambiBoxError;
        private set => SetField(ref _ambiBoxError, value);
    }

    public ICommand CloseAmbiBoxCommand { get; }
    public ICommand RemoveAmbiBoxAutostartCommand { get; }
    public ICommand UndoAutostartCommand { get; }
    public ICommand DismissImportCommand { get; }
    public ICommand DismissAutostartBarCommand { get; }
    public ICommand DismissAutostartRemovedCommand { get; }

    private async Task CloseAmbiBoxAsync()
    {
        ClosingAmbiBox = true;
        AmbiBoxError = null;
        try
        {
            if (!await _host.CloseAmbiBoxAsync())
                AmbiBoxError = "AmbiBox could not be closed. Please close it from its tray icon.";
        }
        finally
        {
            ClosingAmbiBox = false;
            RefreshStatus();
        }
    }

    private void OnAmbiBoxStateChanged()
    {
        OnPropertyChanged(nameof(ShowAmbiBoxRunningBar));
        OnPropertyChanged(nameof(AmbiBoxInAutostart));
        OnPropertyChanged(nameof(ShowAmbiBoxAutostartBar));
        RefreshStatus();
    }

    // ------------------------------------------------------------------ test patterns

    public bool ShowWhite
    {
        get => _host.ActiveOverride == UiOverride.White;
        set => _host.SetOverride(value ? UiOverride.White : UiOverride.None);
    }

    public bool IdentifyLeds
    {
        get => _host.ActiveOverride == UiOverride.Identify;
        set => _host.SetOverride(value ? UiOverride.Identify : UiOverride.None);
    }

    public bool TestColors
    {
        get => _host.ActiveOverride == UiOverride.TestColors;
        set => _host.SetOverride(value ? UiOverride.TestColors : UiOverride.None);
    }

    public bool CalibrationActive => _host.CalibrationActive;

    private void OnOverrideChanged()
    {
        OnPropertyChanged(nameof(ShowWhite));
        OnPropertyChanged(nameof(IdentifyLeds));
        OnPropertyChanged(nameof(TestColors));
        OnPropertyChanged(nameof(CalibrationActive));
        if (!_host.CalibrationActive)
        {
            _ = RefreshPhoneCertAsync(); // the phone may have been set up during the calibration
            OnPropertyChanged(nameof(PreviousWhiteBalanceText));
        }
        CommandManager.InvalidateRequerySuggested();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ ports / monitors

    public ObservableCollection<Option<string>> PortOptions { get; } = new();
    public ObservableCollection<Option<string>> MonitorOptions { get; } = new();

    public IReadOnlyList<string> BaudRates { get; } =
        ["115200", "230400", "250000", "460800", "500000", "921600", "1000000", "2000000", "57600", "38400", "19200", "9600"];

    public IReadOnlyList<Option<ColorOrder>> ColorOrders { get; } =
        [.. Enum.GetValues<ColorOrder>().Select(o => new Option<ColorOrder>(o, o.ToString()))];

    public IReadOnlyList<Option<int>> FpsOptions { get; } =
    [
        new(0, "Automatic (fastest)"),
        new(60, "60 fps"), new(50, "50 fps"), new(30, "30 fps"), new(25, "25 fps"), new(20, "20 fps"), new(15, "15 fps"),
    ];

    public IReadOnlyList<Option<StartCorner>> StartCorners { get; } =
    [
        new(StartCorner.BottomLeft, "Bottom left"), new(StartCorner.BottomRight, "Bottom right"),
        new(StartCorner.TopLeft, "Top left"), new(StartCorner.TopRight, "Top right"),
    ];

    public IReadOnlyList<Option<int>> Directions { get; } =
    [
        new(0, "Counter-clockwise"), new(1, "Clockwise"),
    ];

    private void OnDevicesChanged() => RefreshPortsAsync();

    private async void RefreshPortsAsync()
    {
        try
        {
            var ports = await Task.Run(() => _host.Device.ListPorts());
            _ports = ports;
            var selected = S.Port;
            PortOptions.Clear();
            PortOptions.Add(new(DeviceSettings.AutoPort, "Automatic (recommended)"));
            foreach (var p in ports) PortOptions.Add(new(p.PortName, $"{p.PortName} – {p.Description}"));
            if (!PortOptions.Any(o => o.Value == selected))
                PortOptions.Add(new(selected, $"{selected} (not connected)"));
            OnPropertyChanged(nameof(PortOptions));
            S.RefreshPort();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing serial ports failed: {ex.Message}");
        }
    }

    private void RefreshMonitors()
    {
        try
        {
            var monitors = _host.Sampler.GetMonitors();
            MonitorOptions.Clear();
            var primary = monitors.FirstOrDefault(m => m.IsPrimary);
            MonitorOptions.Add(new("", primary is null ? "Primary monitor" : $"Primary monitor – {primary.FriendlyName}"));
            foreach (var m in monitors) MonitorOptions.Add(new(m.Id, m.FriendlyName + (m.IsPrimary ? " – primary" : "")));
            if (!string.IsNullOrEmpty(S.MonitorId) && !MonitorOptions.Any(o => o.Value == S.MonitorId))
                MonitorOptions.Add(new(S.MonitorId, "Saved monitor (not connected)"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing monitors failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ misc commands

    public ICommand StartCalibrationCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenSettingsFolderCommand { get; }
    public ICommand RestorePreviousWhiteBalanceCommand { get; }

    public string PreviousWhiteBalanceText => _host.PreviousCalibration is { } p
        ? $"Before the last calibration: red {p.WhiteBalance.R:P0} · green {p.WhiteBalance.G:P0} · blue {p.WhiteBalance.B:P0}"
          + $", dark tones {(IsNeutral(p.ChannelGamma) ? "off" : "on")}, color correction {(p.ColorMatrix is null ? "off" : "on")}"
        : "Available after you have calibrated in this session.";

    private static bool IsNeutral((double R, double G, double B) g) =>
        Math.Abs(g.R - 1) < 0.005 && Math.Abs(g.G - 1) < 0.005 && Math.Abs(g.B - 1) < 0.005;

    public string ColorMatrixText
    {
        get
        {
            var m = _host.Settings.Color.ColorMatrix;
            if (m is null) return "Off";
            double dev = 0;
            for (int i = 0; i < 9; i++) dev = Math.Max(dev, Math.Abs(m[i] - (i % 4 == 0 ? 1 : 0)));
            return $"On (up to {dev:P0} mixing)";
        }
    }

    // ------------------------------------------------------------------ phone certificate (live camera)

    private string _phoneCertStatus = "Checking…";
    public string PhoneCertStatusText { get => _phoneCertStatus; private set => SetField(ref _phoneCertStatus, value); }
    private string? _phoneCertMessage;
    public string? PhoneCertMessage { get => _phoneCertMessage; private set => SetField(ref _phoneCertMessage, value); }
    public ICommand ResetPhoneCertCommand { get; }

    private async Task RefreshPhoneCertAsync()
    {
        var ca = _host.PhoneCa;
        var (exists, trusted) = await Task.Run(() => (ca.Exists, ca.PhoneTrustedAt));
        PhoneCertStatusText = trusted is { } t
            ? $"Live camera: ready ✓ (phone set up {t.ToLocalTime():d})"
            : exists ? "Live camera: one-time phone setup needed (the phone guides you)"
                     : "Live camera: one-time phone setup needed (the phone guides you; the certificate is created at the first start)";
    }

    private async Task ResetPhoneCertAsync()
    {
        if (_host.CalibrationActive)
        {
            PhoneCertMessage = "Close the running calibration first.";
            return;
        }
        var answer = MessageBox.Show(
            "This deletes RambiBox's local certificate and creates a new one. Your phone then needs the one-time setup again " +
            "(you can remove the old profile in the phone's Settings → General → VPN & Device Management).\n\nReset the phone certificate?",
            "RambiBox", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        await Task.Run(() => _host.PhoneCa.Reset());
        PhoneCertMessage = "Certificate reset. The phone will be guided through the setup at the next calibration.";
        await RefreshPhoneCertAsync();
    }

    public string ChannelGammaText
    {
        get
        {
            var c = _host.Settings.Color;
            bool neutral = Math.Abs(c.ChannelGammaR - 1) < 0.005 && Math.Abs(c.ChannelGammaG - 1) < 0.005 && Math.Abs(c.ChannelGammaB - 1) < 0.005;
            return neutral ? "None (not measured yet)" : $"Red {c.ChannelGammaR:0.00} · Green {c.ChannelGammaG:0.00} · Blue {c.ChannelGammaB:0.00}";
        }
    }

    private void RestorePreviousWhiteBalance()
    {
        if (_host.PreviousCalibration is not { } p) return;
        _host.PreviousCalibration = null;
        _host.ApplyCalibration(p); // white balance, dark tones and color correction
        OnPropertyChanged(nameof(PreviousWhiteBalanceText));
    }

    public void RefreshCalibrationInfo()
    {
        OnPropertyChanged(nameof(PreviousWhiteBalanceText));
        OnPropertyChanged(nameof(ChannelGammaText));
        OnPropertyChanged(nameof(ColorMatrixText));
        _ = RefreshFirewallAsync();
        _ = RefreshPhoneCertAsync();
    }

    // ------------------------------------------------------------------ firewall (phone calibration)

    private bool _firewallBusy;
    private bool? _firewallAllowed;
    public bool FirewallAllowed => _firewallAllowed == true;
    public string FirewallStatusText => _firewallBusy ? "Waiting for permission…"
        : _firewallAllowed switch { true => "Firewall: allowed ✓", false => "Not allowed yet", _ => "Checking…" };
    private string? _firewallMessage;
    public string? FirewallMessage { get => _firewallMessage; private set => SetField(ref _firewallMessage, value); }
    public ICommand AllowFirewallCommand { get; }

    public async Task RefreshFirewallAsync()
    {
        _firewallAllowed = await FirewallRule.ExistsAsync();
        OnPropertyChanged(nameof(FirewallAllowed));
        OnPropertyChanged(nameof(FirewallStatusText));
    }

    private async Task AllowFirewallAsync()
    {
        _firewallBusy = true;
        FirewallMessage = null;
        OnPropertyChanged(nameof(FirewallStatusText));
        try
        {
            var result = await FirewallRule.AddAsync();
            FirewallMessage = result switch
            {
                null => "Cancelled. Windows will ask once when you start the calibration instead.",
                false => "The firewall rule could not be added. Windows will ask when you start the calibration.",
                _ => null,
            };
        }
        finally
        {
            _firewallBusy = false;
            await RefreshFirewallAsync();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string VersionText => $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}{(IsSimulated ? " · simulated LED device" : "")}";
    public string SettingsPath => _host.Store.FilePath;
    public string LogPath => Log.FilePath;

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Could not open {path}: {ex.Message}"); }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _host.DeviceStatusChanged -= OnDeviceStatusChanged;
        _host.DevicesChanged -= OnDevicesChanged;
        _host.OverrideChanged -= OnOverrideChanged;
        _host.AmbiBoxStateChanged -= OnAmbiBoxStateChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        S.Dispose();
    }
}
