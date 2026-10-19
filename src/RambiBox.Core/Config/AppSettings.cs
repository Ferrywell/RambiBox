using System.Text.Json.Serialization;

namespace RambiBox.Core.Config;

/// <summary>What the LEDs show.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LightMode>))]
public enum LightMode
{
    /// <summary>Colors follow the screen edges.</summary>
    Ambilight,
    /// <summary>One fixed color.</summary>
    StaticColor,
    /// <summary>Slow animated mood light.</summary>
    Mood,
}

[JsonConverter(typeof(JsonStringEnumConverter<ColorOrder>))]
public enum ColorOrder { RGB, RBG, GRB, GBR, BRG, BGR }

[JsonConverter(typeof(JsonStringEnumConverter<MoodEffect>))]
public enum MoodEffect
{
    /// <summary>Slowly cycles through the hue wheel, all LEDs the same color.</summary>
    ColorCycle,
    /// <summary>Rainbow that rotates around the screen.</summary>
    Rainbow,
    /// <summary>Static color that slowly pulses in brightness.</summary>
    Breathe,
}

[JsonConverter(typeof(JsonStringEnumConverter<StartCorner>))]
public enum StartCorner { BottomLeft, BottomRight, TopLeft, TopRight }

/// <summary>
/// Root settings object. Persisted as JSON in %AppData%\RambiBox\settings.json.
/// All classes are plain mutable DTOs; the engine always works on a deep clone (<see cref="Clone"/>).
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Master on/off switch.</summary>
    public bool Enabled { get; set; } = true;

    public LightMode Mode { get; set; } = LightMode.Ambilight;

    public DeviceSettings Device { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public ColorSettings Color { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public StaticSettings Static { get; set; } = new();
    public GeneralSettings General { get; set; } = new();

    public AppSettings Clone() => SettingsSerializer.Clone(this);
}

public sealed class DeviceSettings
{
    /// <summary>"Auto" for automatic detection, otherwise a port name like "COM3".</summary>
    public string Port { get; set; } = AutoPort;
    public const string AutoPort = "Auto";

    public int BaudRate { get; set; } = 115200;
    public ColorOrder ColorOrder { get; set; } = ColorOrder.RGB;

    /// <summary>Remembered by auto-detect: last port that answered as an Adalight device (tried first).</summary>
    public string? LastPort { get; set; }
    /// <summary>Remembered by auto-detect: USB VID/PID ("1A86:7523") of the last working device.</summary>
    public string? LastUsbId { get; set; }
}

/// <summary>A capture rectangle for one LED, normalized to 0..1 of the monitor (left/top inclusive).</summary>
public sealed record LedZone(float Left, float Top, float Right, float Bottom, bool Enabled = true)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

public sealed class LayoutSettings
{
    /// <summary>
    /// The authoritative zone list, in LED order (index 0 = first LED on the strip).
    /// Count of this list = number of LEDs.
    /// </summary>
    public List<LedZone> Zones { get; set; } = new();

    // Parameters of the simple layout generator (see LayoutGenerator). Kept so the UI can show/edit them.
    public int Bottom { get; set; } = 37;
    public int Right { get; set; } = 19;
    public int Top { get; set; } = 37;
    public int Left { get; set; } = 19;
    public StartCorner StartCorner { get; set; } = StartCorner.BottomLeft;
    /// <summary>Direction as seen from the front of the screen.</summary>
    public bool Clockwise { get; set; } = false;
    /// <summary>Depth of top/bottom zones as fraction of screen height.</summary>
    public float DepthHorizontal { get; set; } = 0.0527f;
    /// <summary>Depth of left/right zones as fraction of screen width.</summary>
    public float DepthVertical { get; set; } = 0.027f;
    /// <summary>Skip the first N LEDs on the strip (e.g. LEDs behind the stand). Kept black.</summary>
    public int Offset { get; set; } = 0;

    public int LedCount => Zones.Count;
}

public sealed class ColorSettings
{
    /// <summary>Overall brightness 0..1.</summary>
    public double Brightness { get; set; } = 1.0;
    /// <summary>Saturation multiplier, 1 = unchanged. AmbiBox "Saturation 130" = 1.3.</summary>
    public double Saturation { get; set; } = 1.3;
    /// <summary>Gamma applied to the captured color, 1 = unchanged. AmbiBox "Gamma 100" = 1.0.</summary>
    public double Gamma { get; set; } = 1.0;
    /// <summary>Hue rotation in degrees (-180..180).</summary>
    public double HueShift { get; set; } = 0;
    /// <summary>White balance per channel, 0..1. Imported from AmbiBox (R64 G100 B51).</summary>
    public double WhiteR { get; set; } = 0.64;
    public double WhiteG { get; set; } = 1.0;
    public double WhiteB { get; set; } = 0.51;
    /// <summary>Lowest output level 0..1 so LEDs never go fully dark (0 = off allowed).</summary>
    public double MinBrightness { get; set; } = 0.0;
    /// <summary>Colors with max channel below this (0..1) are treated as black. Removes flicker in dark scenes.</summary>
    public double DarkThreshold { get; set; } = 10 / 255.0;
    /// <summary>Smoothing time constant in milliseconds (0 = off). Frame-rate independent.</summary>
    public int SmoothingMs { get; set; } = 100;
}

public sealed class CaptureSettings
{
    /// <summary>Stable monitor id (see MonitorInfo.Id); empty = primary monitor.</summary>
    public string MonitorId { get; set; } = "";
    /// <summary>Upper bound for updates per second; 0 = as fast as the LED link allows.</summary>
    public int MaxFps { get; set; } = 0;
    /// <summary>Detect letterbox/pillarbox black bars and sample the picture instead of the bars.</summary>
    public bool BlackBarDetection { get; set; } = true;
}

public sealed class StaticSettings
{
    /// <summary>0xRRGGBB.</summary>
    public int Color { get; set; } = 0x00FF00;
    public MoodEffect MoodEffect { get; set; } = MoodEffect.ColorCycle;
    /// <summary>Animation speed multiplier, 1 = default (about one full cycle per minute).</summary>
    public double MoodSpeed { get; set; } = 1.0;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    /// <summary>Turn LEDs off while the display is off / PC sleeps / workstation is locked.</summary>
    public bool OffWhenDisplayOff { get; set; } = true;
    public bool OffWhenLocked { get; set; } = false;
    /// <summary>True once the first-run import/setup has completed.</summary>
    public bool FirstRunDone { get; set; } = false;
}
