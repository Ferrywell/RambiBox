using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>How a controller is attached; the device page groups the type picker by this.</summary>
public enum DeviceFamily
{
    UsbSerial,
    UsbHid,
    Network,
    Other,
}

/// <summary>Display facts about one <see cref="DeviceType"/> (UI text, defaults). Pure data, no driver needed.</summary>
/// <param name="Type">The device type.</param>
/// <param name="Family">How it is attached.</param>
/// <param name="Name">Short display name ("WLED").</param>
/// <param name="Description">One line for the type picker.</param>
public sealed record DeviceTypeInfo(DeviceType Type, DeviceFamily Family, string Name, string Description)
{
    public bool IsSerial => Family == DeviceFamily.UsbSerial;
    public bool IsNetwork => Family == DeviceFamily.Network;
}

/// <summary>
/// The catalogue of device families shown in the UI, plus the small rules around them (default baud rates and UDP
/// ports, how to describe the connection target in status texts). App-independent so it can be unit tested.
/// </summary>
public static class DeviceCatalog
{
    public const int WledDefaultPort = 21324;
    public const int DdpDefaultPort = 4048;
    public const int E131DefaultPort = 5568;
    /// <summary>E1.31: RGB LEDs per universe (512 channels / 3).</summary>
    public const int E131LedsPerUniverse = 170;

    /// <summary>All types in picker order (grouped by family).</summary>
    public static IReadOnlyList<DeviceTypeInfo> All { get; } =
    [
        new(DeviceType.Adalight, DeviceFamily.UsbSerial, "Adalight", "Arduino + WS281x strip – the classic DIY setup (also what AmbiBox uses)."),
        new(DeviceType.Ardulight, DeviceFamily.UsbSerial, "Ardulight", "Arduino with the Ardulight sketch from Prismatik."),
        new(DeviceType.Awa, DeviceFamily.UsbSerial, "AWA / HyperSerial", "ESP8266/ESP32 with HyperSerial firmware – very high baud rates, lots of LEDs."),
        new(DeviceType.Tpm2, DeviceFamily.UsbSerial, "TPM2", "Controllers that speak the TPM2 serial protocol."),
        new(DeviceType.Lightpack, DeviceFamily.UsbHid, "Lightpack", "Lightpack and Lightpack 2 USB devices (no COM port needed)."),
        new(DeviceType.Wled, DeviceFamily.Network, "WLED", "ESP8266/ESP32 with WLED over Wi-Fi or Ethernet (realtime UDP)."),
        new(DeviceType.Ddp, DeviceFamily.Network, "DDP", "Distributed Display Protocol – WLED, xLights controllers, ESPixelStick."),
        new(DeviceType.E131, DeviceFamily.Network, "E1.31 / sACN", "DMX over Ethernet – Falcon, WLED and other pixel controllers."),
        new(DeviceType.AlienFx, DeviceFamily.Other, "Alienware AlienFX", "The built-in lighting of Alienware PCs – requires Alienware Command Center (LightFX)."),
        new(DeviceType.Virtual, DeviceFamily.Other, "Virtual (no hardware)", "Shows the colors only in the app's preview – for testing and demos."),
    ];

    public static DeviceTypeInfo Get(DeviceType type) =>
        All.FirstOrDefault(i => i.Type == type) ?? new(type, DeviceFamily.Other, type.ToString(), "");

    public static string FamilyName(DeviceFamily f) => f switch
    {
        DeviceFamily.UsbSerial => "USB serial",
        DeviceFamily.UsbHid => "USB HID",
        DeviceFamily.Network => "Network",
        _ => "Other",
    };

    public static bool IsSerial(DeviceType t) => Get(t).IsSerial;
    public static bool IsNetwork(DeviceType t) => Get(t).IsNetwork;

    /// <summary>The baud rate a controller of this type usually runs at (firmware default).</summary>
    public static int DefaultBaud(DeviceType t) => t == DeviceType.Awa ? 2_000_000 : 115_200;

    /// <summary>Baud rate presets for the editable baud box, most common first.</summary>
    public static IReadOnlyList<int> BaudPresets(DeviceType t) => t switch
    {
        DeviceType.Awa => [2_000_000, 1_500_000, 1_000_000, 921_600],
        _ => [115_200, 230_400, 250_000, 460_800, 500_000, 921_600, 1_000_000, 2_000_000, 57_600, 38_400, 19_200, 9_600],
    };

    /// <summary>
    /// The baud rate to use after switching from <paramref name="from"/> to <paramref name="to"/>: when the current
    /// value is still the old type's default (i.e. the user never tuned it) it follows the new type's default
    /// (Adalight 115200 → AWA 2000000); a hand-picked rate is kept.
    /// </summary>
    public static int BaudAfterTypeChange(DeviceType from, DeviceType to, int currentBaud)
    {
        if (!IsSerial(to)) return currentBaud;
        if (!IsSerial(from) || currentBaud == DefaultBaud(from) || currentBaud <= 0) return DefaultBaud(to);
        return currentBaud;
    }

    /// <summary>UDP port used when <see cref="DeviceSettings.NetworkPort"/> is 0.</summary>
    public static int DefaultNetworkPort(DeviceType t) => t switch
    {
        DeviceType.Wled => WledDefaultPort,
        DeviceType.Ddp => DdpDefaultPort,
        DeviceType.E131 => E131DefaultPort,
        _ => 0,
    };

    /// <summary>"Universe 1" / "Universes 1–3" for <paramref name="ledCount"/> LEDs starting at <paramref name="startUniverse"/>.</summary>
    public static string E131UniverseRange(int startUniverse, int ledCount)
    {
        int n = Math.Max(1, (Math.Max(1, ledCount) + E131LedsPerUniverse - 1) / E131LedsPerUniverse);
        return n == 1 ? $"universe {startUniverse}" : $"universes {startUniverse}–{startUniverse + n - 1}";
    }

    /// <summary>
    /// The device to apply when the user picks a found controller: WLED discovery reports WLED entries, but a WLED
    /// controller also speaks DDP and E1.31 – when one of those protocols is selected it is kept.
    /// </summary>
    public static DiscoveredDevice ForCurrentProtocol(DiscoveredDevice found, DeviceSettings current) =>
        found.Type == DeviceType.Wled && current.Type is DeviceType.Ddp or DeviceType.E131 ? found with { Type = current.Type } : found;

    // ------------------------------------------------------------------ status texts

    /// <summary>
    /// What the app is talking to, for status lines: "COM3", "WLED-TV (192.168.2.40)", "192.168.2.40",
    /// "sACN multicast", "Lightpack", "the virtual device".
    /// </summary>
    /// <param name="s">The device settings.</param>
    /// <param name="activePort">The driver's <see cref="ILedDevice.PortName"/> (the port auto-detect found, …).</param>
    public static string Target(DeviceSettings s, string? activePort)
    {
        var info = Get(s.Type);
        switch (info.Family)
        {
            case DeviceFamily.UsbSerial:
                if (!string.IsNullOrWhiteSpace(activePort)) return activePort;
                return string.Equals(s.Port, DeviceSettings.AutoPort, StringComparison.OrdinalIgnoreCase) ? "USB" : s.Port;
            case DeviceFamily.Network:
                if (s.Type == DeviceType.E131 && s.E131Multicast) return "sACN multicast";
                if (!string.IsNullOrWhiteSpace(s.Name)) return s.Name;
                if (!string.IsNullOrWhiteSpace(s.Host)) return s.Host;
                return string.IsNullOrWhiteSpace(activePort) ? "no address set" : activePort;
            case DeviceFamily.UsbHid:
                if (!string.IsNullOrWhiteSpace(activePort)) return activePort; // "Lightpack ABC123", "Lightpack (2 devices)"
                if (!string.IsNullOrWhiteSpace(s.Name)) return s.Name;
                return string.IsNullOrWhiteSpace(s.DeviceId) || s.DeviceId == "*" ? "Lightpack" : $"Lightpack {s.DeviceId}";
            default:
                return s.Type == DeviceType.Virtual ? "the virtual device" : info.Name;
        }
    }

    /// <summary>"Connected on COM3" (serial) or "Connected to WLED-TV (192.168.2.40)".</summary>
    public static string ConnectedText(DeviceSettings s, string? activePort)
    {
        string target = Target(s, activePort);
        return s.Type switch
        {
            DeviceType.Virtual => "Virtual device (preview only)",
            DeviceType.AlienFx => "Connected to Alienware lighting",
            _ when IsSerial(s.Type) => $"Connected on {target}",
            _ => $"Connected to {target}",
        };
    }

    /// <summary>Status line while the controller is not reachable yet.</summary>
    public static string SearchingText(DeviceSettings s)
    {
        var info = Get(s.Type);
        return info.Family switch
        {
            DeviceFamily.UsbSerial => string.Equals(s.Port, DeviceSettings.AutoPort, StringComparison.OrdinalIgnoreCase)
                ? "Looking for the LED controller…" : $"Waiting for the LED controller on {s.Port}…",
            DeviceFamily.UsbHid => "Looking for the Lightpack…",
            DeviceFamily.Network when s.Type == DeviceType.E131 && s.E131Multicast => "Starting E1.31 multicast…",
            DeviceFamily.Network when string.IsNullOrWhiteSpace(s.Host) => $"Set the address of your {info.Name} controller",
            DeviceFamily.Network => $"Looking for {info.Name} at {s.Name ?? s.Host}…",
            _ when s.Type == DeviceType.AlienFx => "Looking for Alienware lighting…",
            _ => "Starting…",
        };
    }

    /// <summary>Second line with a hint while searching.</summary>
    public static string SearchingDetail(DeviceSettings s) => Get(s.Type).Family switch
    {
        DeviceFamily.UsbSerial => "Plug in the USB cable of your LED strip. RambiBox finds it automatically.",
        DeviceFamily.UsbHid => "Plug in the Lightpack's USB cable.",
        DeviceFamily.Network when string.IsNullOrWhiteSpace(s.Host) && !(s.Type == DeviceType.E131 && s.E131Multicast) =>
            "Use Find devices on the Device page, or enter its IP address.",
        DeviceFamily.Network when s.Type == DeviceType.Wled =>
            "Check that it is powered on and on the same network, and that \"Receive UDP realtime\" is on in WLED's Sync settings.",
        DeviceFamily.Network => "Check that the controller is powered on and on the same network as this PC.",
        _ when s.Type == DeviceType.AlienFx => "Requires Alienware Command Center (LightFX) on an Alienware PC.",
        _ => "",
    };
}
