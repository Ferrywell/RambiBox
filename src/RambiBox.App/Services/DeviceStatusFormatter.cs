using RambiBox.App.ViewModels;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Engine;

namespace RambiBox.App.Services;

/// <summary>One status line for the Home page, the navigation footer and the tray tooltip.</summary>
public sealed record StatusLine(string Text, string Detail, StatusLevel Level, bool CloseAmbiBox);

/// <summary>
/// Turns the engine stats into user-facing status text for every device family ("Connected on COM3 · 33 fps",
/// "Connected to WLED-TV (192.168.2.40) · 60 fps", "Looking for WLED at 192.168.2.40…").
/// </summary>
public static class DeviceStatusFormatter
{
    /// <summary>True when the configured type has no driver in this build (the virtual device stands in).</summary>
    public static bool IsUnsupported(AppHost host) =>
        !LedDeviceFactory.IsSupported(host.Settings.Device.Type);

    public static StatusLine Describe(AppHost host, EngineStats st)
    {
        var s = host.Settings;
        var dev = s.Device;
        bool serial = DeviceCatalog.IsSerial(dev.Type);
        string target = DeviceCatalog.Target(dev, st.PortName);
        string connected = DeviceCatalog.ConnectedText(dev, st.PortName);

        if (IsUnsupported(host))
            return new($"{DeviceCatalog.Get(dev.Type).Name} isn't supported in this version yet",
                "The colors are shown in the live preview only. Choose another controller type on the Device page.",
                StatusLevel.Problem, false);

        switch (st.DeviceStatus)
        {
            case DeviceStatus.Connected:
                if (st.OverrideActive)
                    return new($"Showing a test pattern on {target}", "", StatusLevel.Busy, false);
                if (!s.Enabled)
                    return new("Lights are off", connected, StatusLevel.Off, false);
                if ((st.Paused & ~PauseReason.UserOff) != PauseReason.None)
                    return new($"Paused – {DescribePause(st.Paused)}", connected, StatusLevel.Off, false);
                string fps = st.OutputFps >= 1.5 ? $"{st.OutputFps:0} fps" : "idle (nothing changing)";
                string text = $"{connected} · {fps}";
                if (s.Mode == LightMode.Ambilight && st.CaptureMs > 0) text += $" · capture {st.CaptureMs:0.0} ms";
                return new(text, "", StatusLevel.Good, false);

            case DeviceStatus.PortBusy:
                if (serial && host.AmbiBoxRunning)
                    return new($"{target} is in use by AmbiBox", "RambiBox keeps retrying and connects as soon as the port is free.",
                        StatusLevel.Problem, true);
                return new(serial ? $"{target} is in use by another program" : $"{target} is in use by another program (Prismatik?)",
                    "RambiBox keeps retrying and connects as soon as it is free.", StatusLevel.Problem, false);

            case DeviceStatus.Searching:
                return new(DeviceCatalog.SearchingText(dev), DeviceCatalog.SearchingDetail(dev), StatusLevel.Busy, false);

            default:
                return new("Starting…", "", StatusLevel.Busy, false);
        }
    }

    /// <summary>"Adalight · USB-SERIAL CH340 (COM3)", "WLED · WLED-TV (192.168.2.40)" for the device status card.</summary>
    public static string DescribeDevice(AppHost host, EngineStats st, IReadOnlyList<SerialPortInfo> ports)
    {
        var dev = host.Settings.Device;
        var info = DeviceCatalog.Get(dev.Type);
        if (info.IsSerial)
        {
            var p = ports.FirstOrDefault(x => string.Equals(x.PortName, st.PortName, StringComparison.OrdinalIgnoreCase));
            string where = p is not null ? $"{p.Description} ({p.PortName})"
                : st.PortName ?? (string.Equals(dev.Port, DeviceSettings.AutoPort, StringComparison.OrdinalIgnoreCase)
                    ? "no LED controller found yet" : $"{dev.Port} (not found yet)");
            return $"{info.Name} · {where}";
        }
        return dev.Type switch
        {
            DeviceType.Virtual => "No hardware – the colors are shown in the live preview",
            DeviceType.AlienFx => "Alienware AlienFX lighting",
            _ => $"{info.Name} · {DeviceCatalog.Target(dev, st.PortName)}",
        };
    }

    /// <summary>Icon (Segoe Fluent Icons) for a device type.</summary>
    public static string Glyph(DeviceType t) => DeviceCatalog.Get(t).Family switch
    {
        DeviceFamily.UsbSerial => "",  // USB
        DeviceFamily.UsbHid => "",
        DeviceFamily.Network => "",    // Wi-Fi
        _ when t == DeviceType.AlienFx => "", // game controller
        _ => "",                       // monitor
    };

    private static string DescribePause(PauseReason p)
    {
        var parts = new List<string>();
        if (p.HasFlag(PauseReason.DisplayOff)) parts.Add("the display is off");
        if (p.HasFlag(PauseReason.Locked)) parts.Add("the PC is locked");
        if (p.HasFlag(PauseReason.Suspended)) parts.Add("the PC is asleep");
        return string.Join(", ", parts);
    }
}
