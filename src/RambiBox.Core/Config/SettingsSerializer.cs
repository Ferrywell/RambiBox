using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RambiBox.Core.Config;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

public static class SettingsSerializer
{
    public static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings);

    public static AppSettings Deserialize(string json)
    {
        var s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        Normalize(s);
        return s;
    }

    public static AppSettings Clone(AppSettings s) => Deserialize(Serialize(s));

    /// <summary>Repairs missing sections and clamps values to sane ranges (defensive against hand-edited files).</summary>
    public static void Normalize(AppSettings s)
    {
        s.Device ??= new();
        s.Layout ??= new();
        s.Layout.Zones ??= new();
        s.Color ??= new();
        s.Capture ??= new();
        s.Static ??= new();
        s.General ??= new();
        if (string.IsNullOrWhiteSpace(s.Device.Port)) s.Device.Port = DeviceSettings.AutoPort;
        if (s.Device.BaudRate <= 0) s.Device.BaudRate = 115200;
        s.Capture.MonitorId ??= "";

        var c = s.Color;
        c.Brightness = Math.Clamp(c.Brightness, 0, 1);
        c.Saturation = Math.Clamp(c.Saturation, 0, 3);
        c.Gamma = Math.Clamp(c.Gamma, 0.2, 5);
        c.HueShift = Math.Clamp(c.HueShift, -180, 180);
        c.WhiteR = Math.Clamp(c.WhiteR, 0, 1);
        c.WhiteG = Math.Clamp(c.WhiteG, 0, 1);
        c.WhiteB = Math.Clamp(c.WhiteB, 0, 1);
        c.MinBrightness = Math.Clamp(c.MinBrightness, 0, 1);
        c.DarkThreshold = Math.Clamp(c.DarkThreshold, 0, 0.5);
        c.SmoothingMs = Math.Clamp(c.SmoothingMs, 0, 2000);
        s.Capture.MaxFps = Math.Clamp(s.Capture.MaxFps, 0, 240);
        s.Static.MoodSpeed = Math.Clamp(s.Static.MoodSpeed, 0.05, 20);
        s.Layout.Offset = Math.Clamp(s.Layout.Offset, 0, 1000);

        // Zones must be valid rectangles inside 0..1
        for (int i = 0; i < s.Layout.Zones.Count; i++)
        {
            var z = s.Layout.Zones[i];
            float l = Math.Clamp(Math.Min(z.Left, z.Right), 0, 1), r = Math.Clamp(Math.Max(z.Left, z.Right), 0, 1);
            float t = Math.Clamp(Math.Min(z.Top, z.Bottom), 0, 1), b = Math.Clamp(Math.Max(z.Top, z.Bottom), 0, 1);
            s.Layout.Zones[i] = z with { Left = l, Top = t, Right = r, Bottom = b };
        }
        if (s.Layout.Zones.Count > 1000) s.Layout.Zones.RemoveRange(1000, s.Layout.Zones.Count - 1000);
    }
}

/// <summary>Loads and saves settings with atomic writes and a backup copy.</summary>
public sealed class SettingsStore
{
    public string FilePath { get; }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RambiBox", "settings.json");
    }

    public bool Exists => File.Exists(FilePath);

    /// <summary>Loads settings; falls back to the .bak file, then to defaults. Never throws.</summary>
    public AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (File.Exists(path))
                    return SettingsSerializer.Deserialize(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read settings from {path}: {ex.Message}");
            }
        }
        return new AppSettings();
    }

    /// <summary>Writes to a temp file then atomically replaces the real file (keeps .bak). Thread-safe.</summary>
    public void Save(AppSettings s)
    {
        lock (this)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, SettingsSerializer.Serialize(s), new UTF8Encoding(false));
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, FilePath);
        }
    }
}
