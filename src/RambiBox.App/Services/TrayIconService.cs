using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using RambiBox.Core;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Engine;

namespace RambiBox.App.Services;

/// <summary>
/// Notification area icon: left click opens the window, right click shows quick controls.
/// Event driven only (settings / device status / theme changes); no timers while idle.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private enum IconState { On, Off, NotConnected }

    private readonly App _app;
    private readonly AppHost _host;
    private readonly TaskbarIcon _icon;
    private readonly Dictionary<string, System.Drawing.Icon> _iconCache = new();
    private readonly DispatcherTimer _refreshSoon;
    private string? _currentIconKey;

    private MenuItem _lightsOn = null!, _calibrate = null!;
    private readonly List<(MenuItem Item, LightMode Mode)> _modeItems = new();
    private readonly List<(MenuItem Item, int Percent)> _brightnessItems = new();

    public TrayIconService(App app, AppHost host)
    {
        _app = app;
        _host = host;
        _icon = new TaskbarIcon
        {
            ToolTipText = "RambiBox",
            NoLeftClickDelay = true,
            MenuActivation = PopupActivationMode.RightClick,
            ContextMenu = BuildMenu(),
        };
        _icon.TrayLeftMouseUp += (_, _) => _app.ShowMainWindow();
        _icon.TrayMouseMove += (_, _) => UpdateToolTip();
        _icon.TrayBalloonTipClicked += (_, _) => _app.ShowMainWindow();
        _icon.ContextMenu.Opened += (_, _) => SyncMenu();

        // The engine publishes its status text shortly after a device status change; refresh a bit later.
        _refreshSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _refreshSoon.Tick += (_, _) =>
        {
            _refreshSoon.Stop();
            UpdateIcon();
            UpdateToolTip();
        };

        _host.SettingsChanged += OnSettingsChanged;
        _host.DeviceStatusChanged += OnDeviceStatusChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        UpdateIcon();
        // false: do NOT put the process into Windows "efficiency mode" (it would throttle the light engine)
        _icon.ForceCreate(enablesEfficiencyMode: false);
        UpdateToolTip();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        _lightsOn = new MenuItem { Header = "Lights on", IsCheckable = true };
        _lightsOn.Click += (_, _) =>
        {
            _host.Settings.Enabled = _lightsOn.IsChecked;
            _host.Commit(this);
        };
        menu.Items.Add(_lightsOn);

        var mode = new MenuItem { Header = "Mode" };
        foreach (var (label, m) in new[] { ("Screen ambilight", LightMode.Ambilight), ("Static color", LightMode.StaticColor), ("Mood light", LightMode.Mood) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true };
            item.Click += (_, _) =>
            {
                _host.Settings.Mode = m;
                _host.Settings.Enabled = true;
                _host.Commit(this);
            };
            mode.Items.Add(item);
            _modeItems.Add((item, m));
        }
        menu.Items.Add(mode);

        var brightness = new MenuItem { Header = "Brightness" };
        foreach (var pct in new[] { 100, 75, 50, 25 })
        {
            var item = new MenuItem { Header = $"{pct}%", IsCheckable = true };
            item.Click += (_, _) =>
            {
                _host.Settings.Color.Brightness = pct / 100.0;
                _host.Commit(this);
            };
            brightness.Items.Add(item);
            _brightnessItems.Add((item, pct));
        }
        menu.Items.Add(brightness);

        menu.Items.Add(new Separator());
        _calibrate = new MenuItem { Header = "Calibrate colors with phone…" };
        _calibrate.Click += (_, _) => _app.StartCalibration();
        menu.Items.Add(_calibrate);

        var open = new MenuItem { Header = "Open RambiBox", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _app.ShowMainWindow();
        menu.Items.Add(open);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => _app.ExitApp();
        menu.Items.Add(exit);
        return menu;
    }

    private void SyncMenu()
    {
        var s = _host.Settings;
        _lightsOn.IsChecked = s.Enabled;
        foreach (var (item, m) in _modeItems) item.IsChecked = s.Mode == m;
        int pct = (int)Math.Round(s.Color.Brightness * 100);
        foreach (var (item, p) in _brightnessItems) item.IsChecked = Math.Abs(p - pct) < 1;
        _calibrate.IsEnabled = !_host.CalibrationActive;
    }

    private void OnSettingsChanged(object? sender)
    {
        UpdateIcon();
        _refreshSoon.Stop();
        _refreshSoon.Start();
    }

    private void OnDeviceStatusChanged()
    {
        UpdateIcon();
        _refreshSoon.Stop();
        _refreshSoon.Start();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            _icon.Dispatcher.BeginInvoke(() =>
            {
                _currentIconKey = null;
                UpdateIcon();
            });
    }

    private IconState CurrentState()
    {
        if (!_host.Settings.Enabled) return IconState.Off;
        if (_host.Device.Status != DeviceStatus.Connected) return IconState.NotConnected;
        var paused = _host.Engine.Paused;
        return paused == PauseReason.None ? IconState.On : IconState.Off;
    }

    private static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    private void UpdateIcon()
    {
        var state = CurrentState() switch { IconState.On => "on", IconState.Off => "off", _ => "nc" };
        var key = $"{state}_{(TaskbarIsLight() ? "light" : "dark")}";
        if (key == _currentIconKey) return;
        try
        {
            if (!_iconCache.TryGetValue(key, out var icon))
            {
                var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Tray/{key}.ico"));
                int size = Math.Max(16, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON));
                using (info.Stream) icon = new System.Drawing.Icon(info.Stream, size, size);
                _iconCache[key] = icon;
            }
            // TaskbarIcon disposes the icon it replaces, so never hand it the cached instance itself.
            _icon.Icon = (System.Drawing.Icon)icon.Clone();
            _currentIconKey = key;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set tray icon: {ex.Message}");
        }
    }

    private void UpdateToolTip()
    {
        var st = _host.Engine.GetStats();
        // same wording as the Home page, for every device family ("Connected to WLED-TV (192.168.2.40) · 60 fps")
        string text = !_host.Settings.Enabled ? "Lights off" : DeviceStatusFormatter.Describe(_host, st).Text;
        text = $"RambiBox – {text}";
        if (text.Length > 127) text = text[..126] + "…";
        if (_icon.ToolTipText != text) _icon.ToolTipText = text;
    }

    /// <summary>Shows a Windows notification from the tray icon (clicking it opens the window).</summary>
    public void Notify(string title, string message)
    {
        try { _icon.ShowNotification(title, message); }
        catch (Exception ex) { Log.Warn($"Notification failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _refreshSoon.Stop();
        _host.SettingsChanged -= OnSettingsChanged;
        _host.DeviceStatusChanged -= OnDeviceStatusChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Dispose();
        foreach (var i in _iconCache.Values) i.Dispose();
        _iconCache.Clear();
    }
}
