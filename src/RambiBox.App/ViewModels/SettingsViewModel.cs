using System.Runtime.CompilerServices;
using System.Windows.Media;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

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
        if (sender is AppHost) ClearDeviceDrafts(); // e.g. "Use this" device: show the new values
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

    public CaptureMethod CaptureMethod
    {
        get => S.Capture.Method;
        set => Set(S.Capture.Method, value, v => S.Capture.Method = v);
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
        set
        {
            // null comes from the ComboBox while its port list is being rebuilt (USB plug/unplug) — not a user
            // choice; ignoring it keeps a fixed "COM5" from silently turning into "Auto".
            if (value is null) return;
            Set(S.Device.Port, string.IsNullOrWhiteSpace(value) ? DeviceSettings.AutoPort : value, v => S.Device.Port = v);
        }
    }

    public string BaudRateText
    {
        // the AWA driver treats the generic 115200 default as its firmware's 2000000 – show what is actually used
        get => (S.Device.Type == DeviceType.Awa && S.Device.BaudRate == 115_200 ? 2_000_000 : S.Device.BaudRate).ToString();
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

    /// <summary>
    /// The controller family. Switching applies live (Commit → engine → SwitchingLedDevice swaps the driver); the
    /// name/id of a previously found device no longer apply, and an untouched baud rate follows the new protocol.
    /// </summary>
    public DeviceType DeviceType
    {
        get => S.Device.Type;
        set
        {
            var d = S.Device;
            if (d.Type == value || !Enum.IsDefined(value)) return;
            d.BaudRate = DeviceCatalog.BaudAfterTypeChange(d.Type, value, d.BaudRate);
            d.Type = value;
            d.Name = null;
            d.DeviceId = null;
            ClearDeviceDrafts();
            _host.Commit(this);
            RefreshAll(); // every type-dependent property (visibility, presets, texts)
            DeviceTypeChanged?.Invoke();
        }
    }

    /// <summary>Raised after the user picked another device type.</summary>
    public event Action? DeviceTypeChanged;

    private DeviceTypeInfo TypeInfo => DeviceCatalog.Get(S.Device.Type);
    public string DeviceTypeDescription => TypeInfo.Description;
    public string DeviceTypeGlyph => Services.DeviceStatusFormatter.Glyph(S.Device.Type);
    public bool IsSerialType => TypeInfo.IsSerial;
    public bool IsNetworkType => TypeInfo.IsNetwork;
    public bool IsLightpackType => S.Device.Type == DeviceType.Lightpack;
    public bool IsWledType => S.Device.Type == DeviceType.Wled;
    public bool IsE131Type => S.Device.Type == DeviceType.E131;
    public bool IsAlienFxType => S.Device.Type == DeviceType.AlienFx;
    public bool IsVirtualType => S.Device.Type == DeviceType.Virtual;
    /// <summary>Serial and network drivers apply the color order; Lightpack and AlienFX have fixed RGB.</summary>
    public bool ShowColorOrder => IsSerialType || IsNetworkType;

    public string PortDescription => S.Device.Type switch
    {
        DeviceType.Ardulight or DeviceType.Tpm2 =>
            $"{TypeInfo.Name} controllers don't identify themselves: choose the port once. After it has worked, Automatic finds it again on any USB port.",
        DeviceType.Awa => "Automatic finds the HyperSerial controller on any USB port, also after re-plugging. Choose a port only if detection fails.",
        _ => "Automatic finds the controller on any USB port, also after re-plugging. Choose a port only if detection fails.",
    };

    public IReadOnlyList<string> BaudPresets => [.. DeviceCatalog.BaudPresets(S.Device.Type).Select(b => b.ToString())];

    public string BaudRateDescription => S.Device.Type switch
    {
        DeviceType.Awa => "Speed of the serial link. HyperSerial firmware runs at 2000000 by default; 921600–2000000 work well.",
        DeviceType.Ardulight => "Speed of the serial link. Must match the Ardulight sketch (115200 by default).",
        DeviceType.Tpm2 => "Speed of the serial link. Must match your controller's firmware.",
        _ => "Speed of the serial link. Must match your controller's firmware (115200 for most Adalight sketches).",
    };

    // ---- network devices (WLED, DDP, E1.31). Text fields keep an invalid entry visible next to its error message.

    private string? _hostDraft, _hostError, _portDraft, _portError, _universeDraft, _universeError;

    private void ClearDeviceDrafts()
    {
        _hostDraft = _hostError = _portDraft = _portError = _universeDraft = _universeError = null;
    }

    /// <summary>IP address or host name (validated; committed when valid).</summary>
    public string HostText
    {
        get => _hostDraft ?? S.Device.Host ?? "";
        set
        {
            var error = DeviceValidation.ValidateHost(value, out var host);
            if (error is not null)
            {
                _hostDraft = value ?? "";
                SetError(ref _hostError, error, nameof(HostError));
                return;
            }
            _hostDraft = null;
            SetError(ref _hostError, null, nameof(HostError));
            if (string.Equals(S.Device.Host, host, StringComparison.Ordinal)) { OnPropertyChanged(); return; }
            S.Device.Host = host;
            S.Device.Name = null;     // a hand-typed address is no longer the discovered device
            S.Device.DeviceId = null;
            _host.Commit(this);
            OnPropertyChanged();
        }
    }

    public string? HostError => _hostError;

    public string HostDescription => S.Device.Type switch
    {
        DeviceType.Wled => "IP address or name of your WLED controller, e.g. 192.168.1.40 or wled-tv.local. Find devices fills this in.",
        DeviceType.E131 => "IP address of the E1.31 controller (unicast). Not needed with multicast.",
        _ => "IP address or host name of the controller, e.g. 192.168.1.40.",
    };

    public int DefaultNetworkPort => DeviceCatalog.DefaultNetworkPort(S.Device.Type);

    /// <summary>UDP port; empty = the protocol default.</summary>
    public string NetworkPortText
    {
        get => _portDraft ?? (S.Device.NetworkPort == 0 ? "" : S.Device.NetworkPort.ToString());
        set
        {
            var error = DeviceValidation.ValidatePort(value, DefaultNetworkPort, out int port);
            if (error is not null)
            {
                _portDraft = value ?? "";
                SetError(ref _portError, error, nameof(NetworkPortError));
                return;
            }
            _portDraft = null;
            SetError(ref _portError, null, nameof(NetworkPortError));
            if (S.Device.NetworkPort != port)
            {
                S.Device.NetworkPort = port;
                _host.Commit(this);
            }
            OnPropertyChanged();
        }
    }

    public string? NetworkPortError => _portError;

    public string UdpPortDescription => S.Device.Type switch
    {
        DeviceType.Wled => $"Leave empty for WLED's realtime port ({DefaultNetworkPort}). Change it only if you changed it in WLED.",
        DeviceType.Ddp => $"Leave empty for the DDP port ({DefaultNetworkPort}).",
        DeviceType.E131 => $"Leave empty for the E1.31 port ({DefaultNetworkPort}).",
        _ => "",
    };

    public NetworkProtocol Protocol
    {
        get => S.Device.Protocol;
        set => Set(S.Device.Protocol, value, v => S.Device.Protocol = v, nameof(Protocol), nameof(ProtocolDescription));
    }

    /// <summary>LEDs on the strip including skipped ones (what the controller receives).</summary>
    private int StripLeds => S.Layout.Zones.Count + S.Layout.Offset;

    public string ProtocolDescription
    {
        get
        {
            int n = StripLeds;
            string auto = n <= 255 ? "WARLS" : n <= 490 ? "DRGB" : "DNRGB";
            return S.Device.Protocol == NetworkProtocol.Auto
                ? $"WLED's realtime UDP format. Automatic uses {auto} for your {n} LEDs."
                : "WLED's realtime UDP format. WARLS handles up to 255 LEDs, DRGB up to 490, DNRGB any number.";
        }
    }

    /// <summary>1..255 seconds (255 = keep the realtime colors until WLED restarts).</summary>
    public int WledTimeoutSeconds
    {
        get => S.Device.WledTimeoutSeconds;
        set => Set(S.Device.WledTimeoutSeconds, Math.Clamp(value, 1, 255), v => S.Device.WledTimeoutSeconds = v);
    }

    public bool E131Multicast
    {
        get => S.Device.E131Multicast;
        set => Set(S.Device.E131Multicast, value, v => S.Device.E131Multicast = v, nameof(E131Multicast), nameof(HostEnabled));
    }

    /// <summary>The address is not used with E1.31 multicast.</summary>
    public bool HostEnabled => !(S.Device.Type == DeviceType.E131 && S.Device.E131Multicast);

    public string E131UniverseText
    {
        get => _universeDraft ?? S.Device.E131Universe.ToString();
        set
        {
            var error = DeviceValidation.ValidateUniverse(value, out int u);
            if (error is not null)
            {
                _universeDraft = value ?? "";
                SetError(ref _universeError, error, nameof(E131UniverseError));
                return;
            }
            _universeDraft = null;
            SetError(ref _universeError, null, nameof(E131UniverseError));
            if (S.Device.E131Universe != u)
            {
                S.Device.E131Universe = u;
                _host.Commit(this);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(E131UniverseDescription));
        }
    }

    public string? E131UniverseError => _universeError;

    public string E131UniverseDescription =>
        $"First DMX universe (170 LEDs each). Your {StripLeds} LEDs use {DeviceCatalog.E131UniverseRange(S.Device.E131Universe, StripLeds)}.";

    /// <summary>Lightpack (and other multi-device families): "" = the first device found, otherwise its id.</summary>
    public string SelectedDeviceId
    {
        get => S.Device.DeviceId is null or "*" ? "" : S.Device.DeviceId; // "*" = all Lightpacks, like empty
        set
        {
            if (value is null) return; // list being rebuilt
            Set(SelectedDeviceId, value, v =>
            {
                S.Device.DeviceId = v.Length == 0 ? null : v;
                S.Device.Name = null;
            });
        }
    }

    /// <summary>Re-announces <see cref="SelectedDeviceId"/> (after the device list was rebuilt).</summary>
    public void RefreshDeviceId() => OnPropertyChanged(nameof(SelectedDeviceId));

    private void SetError(ref string? field, string? value, string name)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
    }

    /// <summary>
    /// Sets the generated layout to <paramref name="stripLeds"/> LEDs on the strip, keeping the proportions of the sides
    /// (replaces hand-made zones).
    /// </summary>
    public void ResizeLayoutTo(int stripLeds)
    {
        LayoutScaling.ResizeTo(S.Layout, stripLeds);
        _customLayout = false;
        LayoutUnlocked = false;
        _host.Commit(this);
        RefreshAll();
        LayoutChanged?.Invoke();
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

    // ------------------------------------------------------------------ Prismatik API (integrations)

    public bool ApiEnabled
    {
        get => S.Api.Enabled;
        set => Set(S.Api.Enabled, value, v => S.Api.Enabled = v, nameof(ApiEnabled), nameof(ApiStatusText), nameof(ApiKeyHintVisible));
    }

    public string ApiPortText
    {
        get => S.Api.Port.ToString();
        set
        {
            if (int.TryParse(value?.Trim(), out int port) && port is >= 1 and <= 65535)
                Set(S.Api.Port, port, v => S.Api.Port = v, nameof(ApiPortText), nameof(ApiStatusText));
            else
                OnPropertyChanged(); // revert the text box
        }
    }

    public bool ApiAllowLan
    {
        get => S.Api.AllowLan;
        set => Set(S.Api.AllowLan, value, v => S.Api.AllowLan = v, nameof(ApiAllowLan), nameof(ApiStatusText), nameof(ApiKeyHintVisible));
    }

    public string ApiKey
    {
        get => S.Api.ApiKey;
        set => Set(S.Api.ApiKey, (value ?? "").Trim(), v => S.Api.ApiKey = v, nameof(ApiKey), nameof(ApiKeyHintVisible));
    }

    /// <summary>"Recommended: set an API key" while other PCs on the network may connect without one.</summary>
    public bool ApiKeyHintVisible => S.Api.Enabled && S.Api.AllowLan && S.Api.ApiKey.Length == 0;

    public System.Windows.Input.ICommand GenerateApiKeyCommand => _generateKey ??= new RelayCommand(() => ApiKey = ApiSettings.GenerateKey());
    private RelayCommand? _generateKey;

    public System.Windows.Input.ICommand CopyApiKeyCommand => _copyKey ??= new RelayCommand(() =>
    {
        try { if (S.Api.ApiKey.Length > 0) System.Windows.Clipboard.SetText(S.Api.ApiKey); }
        catch (Exception ex) { RambiBox.Core.Log.Warn($"Could not copy the API key: {ex.Message}"); }
    });
    private RelayCommand? _copyKey;

    /// <summary>"Listening on 127.0.0.1:3636 · 1 client · locked by 192.168.2.10".</summary>
    public string ApiStatusText
    {
        get
        {
            var st = _host.Api.Status;
            if (!S.Api.Enabled) return "Off";
            if (st.Error is { } err) return err;
            if (!st.Listening) return "Starting…";
            var text = $"Listening on {st.Endpoint} · {st.Clients} client{(st.Clients == 1 ? "" : "s")}";
            if (st.LockedBy is { } who) text += $" · locked by {who}";
            else if (st.Persisting) text += " · showing an integration's last colors";
            return text;
        }
    }

    /// <summary>Called when the API server's state changed.</summary>
    public void RefreshApiStatus() => OnPropertyChanged(nameof(ApiStatusText));

    public void Dispose() => _host.SettingsChanged -= OnSettingsChanged;
}
