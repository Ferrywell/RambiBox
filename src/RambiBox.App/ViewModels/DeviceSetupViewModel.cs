using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using RambiBox.App.Services;
using RambiBox.Core;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.App.ViewModels;

/// <summary>One entry of the device type picker.</summary>
public sealed record DeviceTypeItem(DeviceType Type, string Name, string Description, string FamilyName, string Glyph, bool IsSupported)
{
    public string Badge => IsSupported ? "" : "Coming soon";
}

/// <summary>A controller found by "Find devices", as a card with a "Use this" button.</summary>
public sealed class FoundDeviceItem(DiscoveredDevice device, bool isCurrent, bool isSupported)
{
    public DiscoveredDevice Device { get; } = device;
    public string Name => Device.DisplayName;
    public string Glyph => DeviceStatusFormatter.Glyph(Device.Type);
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            string typeName = DeviceCatalog.Get(Device.Type).Name;
            // "WLED · WLED 0.15.0" reads double: name the type only when the detail doesn't already
            if (!Device.Detail.Contains(Device.Type.ToString(), StringComparison.OrdinalIgnoreCase)
                && !Device.Detail.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                parts.Add(typeName);
            if (!string.IsNullOrWhiteSpace(Device.Detail)) parts.Add(Device.Detail);
            if (Device.LedCount is int n && n > 0) parts.Add($"{n} LEDs");
            return string.Join(" · ", parts);
        }
    }
    public bool IsCurrent { get; } = isCurrent;
    public bool IsSupported { get; } = isSupported;
    public bool CanUse => !IsCurrent && IsSupported;
    public string StateText => IsCurrent ? "In use" : IsSupported ? "" : "Coming soon";
}

/// <summary>
/// Device page logic that is not a plain setting: the grouped type picker, "Find devices" (progress, results,
/// "Use this") and the follow-up offer to match the LED layout to the controller's LED count.
/// </summary>
public sealed class DeviceSetupViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly SettingsViewModel _s;

    public DeviceSetupViewModel(AppHost host, SettingsViewModel s)
    {
        _host = host;
        _s = s;
        // Types without a driver in this build are shown but disabled ("Coming soon"), also in --simulate / --demo.
        static bool Supported(DeviceType t) => LedDeviceFactory.IsSupported(t);
        var items = DeviceCatalog.All
            .Select(i => new DeviceTypeItem(i.Type, i.Name, i.Description, DeviceCatalog.FamilyName(i.Family),
                DeviceStatusFormatter.Glyph(i.Type), Supported(i.Type)))
            .ToList();
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceTypeItem.FamilyName)));
        DeviceTypes = view;

        FindDevicesCommand = new RelayCommand(async () => await FindAsync(), () => !_host.Discovering);
        CancelFindCommand = new RelayCommand(_host.CancelDiscovery);
        UseDeviceCommand = new RelayCommand(p => { if (p is FoundDeviceItem f) Use(f.Device); });
        ResizeLayoutCommand = new RelayCommand(ResizeLayout, () => OfferedLedCount is > 0);
        DismissUsedCommand = new RelayCommand(() => SetUsed(null, null));

        _host.DiscoveryChanged += OnDiscoveryChanged;
        _host.SettingsChanged += OnSettingsChanged;
        _s.DeviceTypeChanged += RefreshLightpacks;
        RebuildResults();
        RebuildDeviceIdOptions();
        RefreshLightpacks();
    }

    public ICollectionView DeviceTypes { get; }

    // ------------------------------------------------------------------ find devices

    public bool IsSearching => _host.Discovering;
    public ObservableCollection<FoundDeviceItem> FoundDevices { get; } = new();
    public bool HasResults => FoundDevices.Count > 0;
    /// <summary>A scan finished and found nothing.</summary>
    public bool ShowNothingFound => _host.HasDiscovered && !_host.Discovering && FoundDevices.Count == 0;
    public string ResultsHeader => FoundDevices.Count == 1 ? "1 controller found" : $"{FoundDevices.Count} controllers found";

    public ICommand FindDevicesCommand { get; }
    public ICommand CancelFindCommand { get; }
    public ICommand UseDeviceCommand { get; }

    private async Task FindAsync()
    {
        SetUsed(null, null);
        await _host.DiscoverAsync();
    }

    private void OnDiscoveryChanged()
    {
        RebuildResults();
        RebuildDeviceIdOptions();
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowNothingFound));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnSettingsChanged(object? sender)
    {
        RebuildResults(); // "In use" follows the settings
        RebuildDeviceIdOptions();
        OnPropertyChanged(nameof(ShowUnsupported));
        OnPropertyChanged(nameof(UnsupportedMessage));
    }

    // ------------------------------------------------------------------ type without a driver in this build

    /// <summary>The configured type has no driver yet (e.g. a settings file from a newer version).</summary>
    public bool ShowUnsupported => DeviceStatusFormatter.IsUnsupported(_host);

    public string UnsupportedMessage =>
        $"{DeviceCatalog.Get(_host.Settings.Device.Type).Name} support is not part of this version yet. Until then the colors are only shown in the live preview.";

    private void RebuildResults()
    {
        var s = _host.Settings.Device;
        FoundDevices.Clear();
        foreach (var d in _host.DiscoveredDevices)
            FoundDevices.Add(new FoundDeviceItem(d, IsInUse(s, d), LedDeviceFactory.IsSupported(d.Type)));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowNothingFound));
        OnPropertyChanged(nameof(ResultsHeader));
    }

    /// <summary>"In use" = exactly this controller is configured (a serial one also when auto-detect connected to its port).</summary>
    private bool IsInUse(DeviceSettings s, DiscoveredDevice d)
    {
        if (d.Type != s.Type) return false;
        var family = DeviceCatalog.Get(d.Type).Family;
        if (family == DeviceFamily.UsbSerial)
            return string.Equals(s.Port, d.Port, StringComparison.OrdinalIgnoreCase)
                   || (string.Equals(s.Port, DeviceSettings.AutoPort, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(_host.Device.PortName, d.Port, StringComparison.OrdinalIgnoreCase));
        if (family == DeviceFamily.UsbHid) // "Automatic" uses the first one found
            return s.DeviceId is not null ? string.Equals(s.DeviceId, d.Id, StringComparison.OrdinalIgnoreCase)
                : _host.DiscoveredDevices.FirstOrDefault(x => x.Type == d.Type) == d;
        return DeviceAutoSwitchPolicy.IsCurrent(s, d);
    }

    // ------------------------------------------------------------------ use a found device + LED count offer

    /// <summary>Applies a found controller and, when it reports another LED count than the layout, offers to adapt.</summary>
    public void Use(DiscoveredDevice d)
    {
        _host.UseDevice(d);
        SetUsed(d.DisplayName, d.LedCount);
    }

    private string? _usedName;
    private int? _offeredLedCount;

    /// <summary>"Now using …" InfoBar (null = hidden).</summary>
    public string? UsedTitle => _usedName is null ? null : $"Now using {_usedName}.";
    public bool ShowUsed => _usedName is not null;
    /// <summary>LED count the controller reports, when it differs from the layout.</summary>
    public int? OfferedLedCount => _offeredLedCount;
    public bool ShowLayoutOffer => _offeredLedCount is > 0;
    public string UsedMessage => _usedMessageOverride ?? (_offeredLedCount is int n
        ? LayoutOfferText(n, _host.Settings.Layout, _s.IsCustomLayout)
        : "The colors are sent to it now. Use Test colors above to check the wiring.");
    public string ResizeLayoutLabel => _offeredLedCount is int n ? $"Set LED layout to {n} LEDs" : "";

    internal static string LayoutOfferText(int reported, LayoutSettings layout, bool custom)
    {
        int current = layout.Zones.Count + layout.Offset;
        return $"It reports {reported} LEDs; your LED layout has {current}."
               + (custom ? " Adapting the layout replaces your hand-made zones." : " The sides keep their proportions.");
    }

    public ICommand ResizeLayoutCommand { get; }
    public ICommand DismissUsedCommand { get; }

    private void SetUsed(string? name, int? reportedLeds)
    {
        _usedName = name;
        _usedMessageOverride = null;
        var l = _host.Settings.Layout;
        _offeredLedCount = name is not null && reportedLeds is int n && n > 0 && n != l.Zones.Count + l.Offset ? n : null;
        foreach (var p in new[] { nameof(UsedTitle), nameof(ShowUsed), nameof(OfferedLedCount), nameof(ShowLayoutOffer),
                                  nameof(UsedMessage), nameof(ResizeLayoutLabel) })
            OnPropertyChanged(p);
        CommandManager.InvalidateRequerySuggested();
    }

    private void ResizeLayout()
    {
        if (_offeredLedCount is not int n) return;
        _s.ResizeLayoutTo(n);
        var l = _host.Settings.Layout;
        Log.Info($"LED layout set to {n} LEDs ({l.Bottom} bottom, {l.Right} right, {l.Top} top, {l.Left} left)");
        _offeredLedCount = null;
        foreach (var p in new[] { nameof(OfferedLedCount), nameof(ShowLayoutOffer), nameof(ResizeLayoutLabel) }) OnPropertyChanged(p);
        _usedMessageOverride = $"LED layout set to {n} LEDs: {l.Bottom} bottom, {l.Right} right, {l.Top} top, {l.Left} left. Check it with Identify LEDs on the LED layout page.";
        OnPropertyChanged(nameof(UsedMessage));
    }

    private string? _usedMessageOverride;

    // ------------------------------------------------------------------ Lightpack device choice

    /// <summary>"Automatic" plus the Lightpacks found (and the saved one when it isn't connected).</summary>
    public ObservableCollection<Option<string>> DeviceIdOptions { get; } = new();

    private void RebuildDeviceIdOptions()
    {
        var type = _host.Settings.Device.Type;
        var saved = _host.Settings.Device.DeviceId ?? "";
        var wanted = new List<Option<string>> { new("", "Automatic (first one found)") };
        var found = _host.DiscoveredDevices.Concat(_hidFound)
            .Where(d => d.Type == type && type == DeviceType.Lightpack)
            .DistinctBy(d => d.Id.ToUpperInvariant());
        foreach (var d in found)
            wanted.Add(new(d.Id, d.DisplayName + (d.LedCount is int n ? $" · {n} LEDs" : "")));
        if (saved.Length > 0 && !wanted.Any(o => string.Equals(o.Value, saved, StringComparison.OrdinalIgnoreCase)))
            wanted.Add(new(saved, $"{saved} (not connected)"));
        if (wanted.SequenceEqual(DeviceIdOptions)) return;
        DeviceIdOptions.Clear();
        foreach (var o in wanted) DeviceIdOptions.Add(o);
        _s.RefreshDeviceId();
    }

    private IReadOnlyList<DiscoveredDevice> _hidFound = [];

    /// <summary>
    /// With Lightpack selected, a quick USB HID scan (only the HID discovery services, ~2 s) fills the device list
    /// without a full "Find devices".
    /// </summary>
    private async void RefreshLightpacks()
    {
        if (_host.Options.Simulate || _host.Settings.Device.Type != DeviceType.Lightpack) return;
        var services = LedDeviceFactory.DiscoveryServices.Where(x => x.Types.Contains(DeviceType.Lightpack)).ToList();
        if (services.Count == 0) return;
        try
        {
            var results = await Task.Run(() => Task.WhenAll(services.Select(x => x.DiscoverAsync(TimeSpan.FromSeconds(2), CancellationToken.None))));
            _hidFound = [.. results.SelectMany(r => r).Where(d => d.Type == DeviceType.Lightpack)];
            RebuildDeviceIdOptions();
        }
        catch (Exception ex)
        {
            Log.Warn($"Looking for Lightpack devices failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _host.DiscoveryChanged -= OnDiscoveryChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        _s.DeviceTypeChanged -= RefreshLightpacks;
    }
}
