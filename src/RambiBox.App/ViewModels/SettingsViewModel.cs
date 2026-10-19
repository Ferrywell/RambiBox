using System.Runtime.CompilerServices;
using System.Windows.Media;
using RambiBox.Core.Config;

namespace RambiBox.App.ViewModels;

/// <summary>
/// Bindable view of <see cref="AppHost.Settings"/> in display units (percentages, ms, …).
/// Every setter validates, writes the live settings and calls <see cref="AppHost.Commit"/>
/// (engine updated immediately, file saved debounced).
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private bool? _customLayout;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        _host.SettingsChanged += OnSettingsChanged;
    }

    private AppSettings S => _host.Settings;

    /// <summary>Defaults used by the per-setting reset buttons.</summary>
    public static ColorSettings ColorDefaults { get; } = new();

    private void OnSettingsChanged(object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        _customLayout = null;
        RefreshAll();
    }

    private void Set<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null, params string[] alsoChanged)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        _host.Commit(this);
        OnPropertyChanged(name);
        foreach (var n in alsoChanged) OnPropertyChanged(n);
    }

    // ------------------------------------------------------------------ master / mode

    public bool Enabled
    {
        get => S.Enabled;
        set => Set(S.Enabled, value, v => S.Enabled = v);
    }

    public LightMode Mode
    {
        get => S.Mode;
        set => Set(S.Mode, value, v => S.Mode = v, nameof(Mode), nameof(IsAmbilight), nameof(IsStaticColor), nameof(IsMood));
    }

    public bool IsAmbilight { get => Mode == LightMode.Ambilight; set { if (value) Mode = LightMode.Ambilight; } }
    public bool IsStaticColor { get => Mode == LightMode.StaticColor; set { if (value) Mode = LightMode.StaticColor; } }
    public bool IsMood { get => Mode == LightMode.Mood; set { if (value) Mode = LightMode.Mood; } }

    // ------------------------------------------------------------------ colors (display units)

    private static double Round(double v, int digits = 0) => Math.Round(v, digits);

    /// <summary>0..100 %</summary>
    public double Brightness
    {
        get => Round(S.Color.Brightness * 100);
        set => Set(Brightness, Math.Clamp(Round(value), 0, 100), v => S.Color.Brightness = v / 100);
    }

    /// <summary>0..300 %</summary>
    public double Saturation
    {
        get => Round(S.Color.Saturation * 100);
        set => Set(Saturation, Math.Clamp(Round(value), 0, 300), v => S.Color.Saturation = v / 100);
    }

    /// <summary>0.2..3</summary>
    public double Gamma
    {
        get => Round(S.Color.Gamma, 2);
        set => Set(Gamma, Math.Clamp(Round(value, 2), 0.2, 3), v => S.Color.Gamma = v);
    }

    /// <summary>-180..180 degrees</summary>
    public double HueShift
    {
        get => Round(S.Color.HueShift);
        set => Set(HueShift, Math.Clamp(Round(value), -180, 180), v => S.Color.HueShift = v);
    }

    /// <summary>0..1000 ms</summary>
    public double SmoothingMs
    {
        get => S.Color.SmoothingMs;
        set => Set(SmoothingMs, Math.Clamp(Round(value / 10) * 10, 0, 1000), v => S.Color.SmoothingMs = (int)v);
    }

    /// <summary>0..25 % of full scale</summary>
    public double DarkThreshold
    {
        get => Round(S.Color.DarkThreshold * 100, 1);
        set => Set(DarkThreshold, Math.Clamp(Round(value, 1), 0, 25), v => S.Color.DarkThreshold = v / 100);
    }

    /// <summary>0..30 %</summary>
    public double MinBrightness
    {
        get => Round(S.Color.MinBrightness * 100);
        set => Set(MinBrightness, Math.Clamp(Round(value), 0, 30), v => S.Color.MinBrightness = v / 100);
    }

    public double WhiteR
    {
        get => Round(S.Color.WhiteR * 100);
        set => Set(WhiteR, Math.Clamp(Round(value), 0, 100), v => S.Color.WhiteR = v / 100, nameof(WhiteR), nameof(WhiteBalanceText));
    }

    public double WhiteG
    {
        get => Round(S.Color.WhiteG * 100);
        set => Set(WhiteG, Math.Clamp(Round(value), 0, 100), v => S.Color.WhiteG = v / 100, nameof(WhiteG), nameof(WhiteBalanceText));
    }

    public double WhiteB
    {
        get => Round(S.Color.WhiteB * 100);
        set => Set(WhiteB, Math.Clamp(Round(value), 0, 100), v => S.Color.WhiteB = v / 100, nameof(WhiteB), nameof(WhiteBalanceText));
    }

    public string WhiteBalanceText => $"Red {WhiteR:0}% · Green {WhiteG:0}% · Blue {WhiteB:0}%";

    /// <summary>Copies all color adjustments from <paramref name="c"/> (reset / import).</summary>
    public void ApplyColorSettings(ColorSettings c)
    {
        S.Color = SettingsSerializer.Clone(new AppSettings { Color = c }).Color;
        _host.Commit(this);
        RefreshAll();
    }

    // ------------------------------------------------------------------ static / mood

    public Color StaticColor
    {
        get
        {
            int c = S.Static.Color;
            return Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        }
        set
        {
            int c = (value.R << 16) | (value.G << 8) | value.B;
            Set(S.Static.Color, c, v => S.Static.Color = v, nameof(StaticColor));
        }
    }

    public MoodEffect MoodEffect
    {
        get => S.Static.MoodEffect;
        set => Set(S.Static.MoodEffect, value, v => S.Static.MoodEffect = v, nameof(MoodEffect),
            nameof(IsColorCycle), nameof(IsRainbow), nameof(IsBreathe));
    }

    public bool IsColorCycle { get => MoodEffect == MoodEffect.ColorCycle; set { if (value) MoodEffect = MoodEffect.ColorCycle; } }
    public bool IsRainbow { get => MoodEffect == MoodEffect.Rainbow; set { if (value) MoodEffect = MoodEffect.Rainbow; } }
    public bool IsBreathe { get => MoodEffect == MoodEffect.Breathe; set { if (value) MoodEffect = MoodEffect.Breathe; } }

    /// <summary>Speed multiplier 0.1..5 (1 = one cycle per minute).</summary>
    public double MoodSpeed
    {
        get => Round(S.Static.MoodSpeed, 2);
        set => Set(MoodSpeed, Math.Clamp(Round(value, 2), 0.1, 5), v => S.Static.MoodSpeed = v);
    }

    // ------------------------------------------------------------------ LED layout

    public int LedsBottom { get => S.Layout.Bottom; set => SetLayout(S.Layout.Bottom, value, v => S.Layout.Bottom = v); }
    public int LedsRight { get => S.Layout.Right; set => SetLayout(S.Layout.Right, value, v => S.Layout.Right = v); }
    public int LedsTop { get => S.Layout.Top; set => SetLayout(S.Layout.Top, value, v => S.Layout.Top = v); }
    public int LedsLeft { get => S.Layout.Left; set => SetLayout(S.Layout.Left, value, v => S.Layout.Left = v); }

    public StartCorner StartCorner
    {
        get => S.Layout.StartCorner;
        set => SetLayout(S.Layout.StartCorner, value, v => S.Layout.StartCorner = v);
    }

    /// <summary>0 = counter-clockwise, 1 = clockwise (as seen from the front).</summary>
    public int DirectionIndex
    {
        get => S.Layout.Clockwise ? 1 : 0;
        set => SetLayout(S.Layout.Clockwise, value == 1, v => S.Layout.Clockwise = v);
    }

    /// <summary>Capture depth of top/bottom zones, % of screen height (1..30).</summary>
    public double DepthHorizontal
    {
        get => Round(S.Layout.DepthHorizontal * 100, 1);
        set => SetLayout(DepthHorizontal, Math.Clamp(Round(value, 1), 1, 30), v => S.Layout.DepthHorizontal = (float)(v / 100));
    }

    /// <summary>Capture depth of left/right zones, % of screen width (1..30).</summary>
    public double DepthVertical
    {
        get => Round(S.Layout.DepthVertical * 100, 1);
        set => SetLayout(DepthVertical, Math.Clamp(Round(value, 1), 1, 30), v => S.Layout.DepthVertical = (float)(v / 100));
    }

    /// <summary>LEDs to skip at the start of the strip (kept dark). Doesn't change the zones.</summary>
    public int Offset
    {
        get => S.Layout.Offset;
        set => Set(S.Layout.Offset, Math.Clamp(value, 0, 300), v => S.Layout.Offset = v, nameof(Offset), nameof(LedSummary));
    }

    public int LedCount => S.Layout.Zones.Count;

    public string LedSummary => S.Layout.Offset > 0
        ? $"{LedCount} LEDs + {S.Layout.Offset} skipped at the start = {LedCount + S.Layout.Offset} on the strip"
        : $"{LedCount} LEDs in total";

    /// <summary>True when the zones don't match what the generator makes from the per-side values (e.g. hand-made in AmbiBox).</summary>
    public bool IsCustomLayout => _customLayout ??= !ZonesMatchGenerator(S.Layout);

    private bool _layoutUnlocked;
    /// <summary>The user confirmed that editing may replace custom zones.</summary>
    public bool LayoutUnlocked
    {
        get => _layoutUnlocked;
        set
        {
            if (SetField(ref _layoutUnlocked, value)) OnPropertyChanged(nameof(CanEditLayout));
        }
    }

    public bool CanEditLayout => !IsCustomLayout || LayoutUnlocked;

    private void SetLayout<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        S.Layout.Bottom = Math.Clamp(S.Layout.Bottom, 0, 300);
        S.Layout.Right = Math.Clamp(S.Layout.Right, 0, 300);
        S.Layout.Top = Math.Clamp(S.Layout.Top, 0, 300);
        S.Layout.Left = Math.Clamp(S.Layout.Left, 0, 300);
        S.Layout.Zones = LayoutGenerator.Generate(S.Layout);
        _customLayout = false;
        _host.Commit(this);
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(LedCount));
        OnPropertyChanged(nameof(LedSummary));
        OnPropertyChanged(nameof(IsCustomLayout));
        OnPropertyChanged(nameof(CanEditLayout));
        LayoutChanged?.Invoke();
    }

    /// <summary>Raised after the zones were regenerated by this view model.</summary>
    public event Action? LayoutChanged;

    public void ReplaceLayout(LayoutSettings layout)
    {
        S.Layout = layout;
        _customLayout = null;
        LayoutUnlocked = false;
        _host.Commit(this);
        RefreshAll();
        LayoutChanged?.Invoke();
    }

    internal static bool ZonesMatchGenerator(LayoutSettings l)
    {
        var g = LayoutGenerator.Generate(l);
        if (g.Count != l.Zones.Count) return false;
        const float tol = 0.002f;
        for (int i = 0; i < g.Count; i++)
        {
            var a = g[i];
            var b = l.Zones[i];
            if (!b.Enabled || Math.Abs(a.Left - b.Left) > tol || Math.Abs(a.Top - b.Top) > tol ||
                Math.Abs(a.Right - b.Right) > tol || Math.Abs(a.Bottom - b.Bottom) > tol)
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ capture

    public string MonitorId
    {
        get => S.Capture.MonitorId;
        set => Set(S.Capture.MonitorId, value ?? "", v => S.Capture.MonitorId = v);
    }

    public bool BlackBarDetection
    {
        get => S.Capture.BlackBarDetection;
        set => Set(S.Capture.BlackBarDetection, value, v => S.Capture.BlackBarDetection = v);
    }

    public int MaxFps
    {
        get => S.Capture.MaxFps;
        set => Set(S.Capture.MaxFps, Math.Clamp(value, 0, 240), v => S.Capture.MaxFps = v);
    }

    // ------------------------------------------------------------------ device

    public string Port
    {
        get => S.Device.Port;
        set => Set(S.Device.Port, string.IsNullOrWhiteSpace(value) ? DeviceSettings.AutoPort : value, v => S.Device.Port = v);
    }

    public string BaudRateText
    {
        get => S.Device.BaudRate.ToString();
        set
        {
            if (int.TryParse(value?.Trim(), out int baud) && baud is >= 1200 and <= 10_000_000)
                Set(S.Device.BaudRate, baud, v => S.Device.BaudRate = v);
            else
                OnPropertyChanged(); // revert the text box
        }
    }

    /// <summary>Re-announces <see cref="Port"/> (after the port list was rebuilt).</summary>
    public void RefreshPort() => OnPropertyChanged(nameof(Port));

    public ColorOrder ColorOrder
    {
        get => S.Device.ColorOrder;
        set => Set(S.Device.ColorOrder, value, v => S.Device.ColorOrder = v);
    }

    // ------------------------------------------------------------------ general

    public bool StartWithWindows
    {
        get => S.General.StartWithWindows;
        set => Set(S.General.StartWithWindows, value, v => S.General.StartWithWindows = v);
    }

    public bool OffWhenDisplayOff
    {
        get => S.General.OffWhenDisplayOff;
        set => Set(S.General.OffWhenDisplayOff, value, v => S.General.OffWhenDisplayOff = v);
    }

    public bool OffWhenLocked
    {
        get => S.General.OffWhenLocked;
        set => Set(S.General.OffWhenLocked, value, v => S.General.OffWhenLocked = v);
    }

    public void Dispose() => _host.SettingsChanged -= OnSettingsChanged;
}
