using System.Diagnostics;
using Microsoft.Win32;

namespace RambiBox.Core.Config;

/// <summary>
/// Reads an existing AmbiBox installation (HKCU\Software\Server IR) and converts it to RambiBox settings,
/// plus helpers to stop AmbiBox and take it out of autostart when switching over.
/// </summary>
public static class AmbiBoxImporter
{
    private const string RootKey = @"Software\Server IR";

    // AmbiBox "AmbilightDevice" index → name (only Adalight-compatible ones are importable)
    private static readonly Dictionary<int, string> DeviceNames = new()
    {
        [0] = "Lightpack", [1] = "Paintpack", [2] = "Atmolight", [3] = "Adalight", [4] = "Ambilight (DIY)",
    };

    public static bool IsInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RootKey + @"\Backlight\Profiles");
        return k is not null;
    }

    /// <summary>Imports the current AmbiBox profile. Returns null when AmbiBox settings are not present or unusable.</summary>
    public static AppSettings? TryImport(out string summary)
    {
        summary = "";
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RootKey);
            using var profiles = Registry.CurrentUser.OpenSubKey(RootKey + @"\Backlight\Profiles");
            using var backlight = Registry.CurrentUser.OpenSubKey(RootKey + @"\Backlight");
            if (root is null || profiles is null) return null;

            int current = Int(profiles, "CurrentProfile", 0);
            string profileName = profiles.GetValue($"ProfileName_{current}") as string ?? "Default";
            using var p = profiles.OpenSubKey(profileName);
            if (p is null) return null;

            var s = new AppSettings();

            // ---- Zones (coordinates are 0..10000)
            var zones = new List<(int led, LedZone zone)>();
            for (int i = 0; i < 1000; i++)
            {
                if (p.GetValue($"Zone_Left_{i}") is not int l) break;
                int t = Int(p, $"Zone_Top_{i}", 0), r = Int(p, $"Zone_Right_{i}", 0), b = Int(p, $"Zone_Bottom_{i}", 0);
                bool enabled = Int(p, $"Zone_Enabled_{i}", 1) != 0;
                int led = Int(p, $"LED_{i}", i);
                zones.Add((led, new LedZone(l / 10000f, t / 10000f, r / 10000f, b / 10000f, enabled)));
            }
            if (zones.Count == 0) return null;
            // LED_i maps zone i to an output LED index; sort so list index == LED index.
            s.Layout.Zones = zones.OrderBy(z => z.led).Select(z => z.zone).ToList();
            InferGeneratorParams(s.Layout);

            // ---- Colors
            var c = s.Color;
            c.Brightness = Int(p, "Lightness", 100) / 100.0;
            c.Saturation = Int(p, "Saturation", 100) / 100.0;
            c.Gamma = Int(p, "Gamma", 100) / 100.0;
            c.HueShift = (Int(p, "Hue", 100) - 100) * 1.8; // AmbiBox hue 0..200 with 100 = neutral
            c.MinBrightness = Int(p, "MinimumLight", 0) / 255.0;
            c.DarkThreshold = Int(p, "Sensitivity", 10) / 255.0;
            // AmbiBox "Dynamics" (smoothness, 0..~50 frames). Map to a time constant in ms.
            c.SmoothingMs = Math.Clamp(Int(p, "Dynamics", 10) * 10, 0, 2000);
            if (backlight is not null)
            {
                // White balance: per-zone adjustments; use the median of the active zones.
                int count = Math.Min(Int(backlight, "Zone_Adjusment_Count", s.Layout.Zones.Count), s.Layout.Zones.Count);
                double Med(string ch) => Median(Enumerable.Range(0, Math.Max(count, 1))
                    .Select(i => Int(backlight, $"Zone_Adjusment_{ch}_{i}", 100) / 100.0));
                c.WhiteR = Math.Clamp(Med("R"), 0, 1);
                c.WhiteG = Math.Clamp(Med("G"), 0, 1);
                c.WhiteB = Math.Clamp(Med("B"), 0, 1);
            }

            // ---- Capture
            int fps = Int(p, "MaximumCaptureFPS", 25);
            s.Capture.MaxFps = 0; // RambiBox paces to the LED link; AmbiBox' cap was a CPU workaround.
            s.Capture.MonitorId = "";

            // ---- Static color: Delphi TColor is 0x00BBGGRR
            int tc = Int(p, "StaticLampColor", 0x00FF00);
            s.Static.Color = ((tc & 0xFF) << 16) | (tc & 0xFF00) | ((tc >> 16) & 0xFF);

            // ---- Device
            int dev = Int(root, "AmbilightDevice", 3);
            int portIndex = Int(root, "DevicePort", -1);
            s.Device.Port = DeviceSettings.AutoPort;
            s.Device.LastPort = portIndex >= 0 ? $"COM{portIndex + 1}" : null;
            s.Device.BaudRate = 115200;
            s.Device.ColorOrder = Int(root, "AdalightLedOrder", 0) switch
            {
                1 => ColorOrder.RBG, 2 => ColorOrder.GRB, 3 => ColorOrder.GBR, 4 => ColorOrder.BRG, 5 => ColorOrder.BGR,
                _ => ColorOrder.RGB,
            };
            s.Enabled = Int(root, "Use Ambilight", 1) != 0;

            string devName = DeviceNames.TryGetValue(dev, out var dn) ? dn : $"device #{dev}";
            summary = $"{s.Layout.Zones.Count} LEDs ({s.Layout.Bottom} bottom, {s.Layout.Right} right, {s.Layout.Top} top, {s.Layout.Left} left), " +
                      $"{devName} on {s.Device.LastPort ?? "auto"}, white balance R{c.WhiteR:P0} G{c.WhiteG:P0} B{c.WhiteB:P0}, " +
                      $"saturation {c.Saturation:P0}, smoothing {c.SmoothingMs} ms (AmbiBox capped at {fps} fps).";
            SettingsSerializer.Normalize(s);
            Log.Info($"Imported AmbiBox profile '{profileName}': {summary}");
            return s;
        }
        catch (Exception ex)
        {
            Log.Warn($"AmbiBox import failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fills the generator parameters (per-side counts etc.) from imported zones so the UI shows sensible values.
    /// The zones themselves stay exactly as imported.
    /// </summary>
    internal static void InferGeneratorParams(LayoutSettings layout)
    {
        int bottom = 0, top = 0, left = 0, right = 0;
        float depthH = 0, depthV = 0;
        var zs = layout.Zones;
        for (int i = 0; i < zs.Count; i++)
        {
            var z = zs[i];
            float cx = (z.Left + z.Right) / 2, cy = (z.Top + z.Bottom) / 2;
            // Zone shapes are ambiguous (AmbiBox makes all zones the same normalized size), so classify by the
            // direction the strip travels: towards the next zone, or from the previous one at corners/duplicates.
            (float dx, float dy) Delta(int j)
            {
                if (j < 0 || j >= zs.Count) return (0, 0);
                var o = zs[j];
                return (MathF.Abs((o.Left + o.Right) / 2 - cx), MathF.Abs((o.Top + o.Bottom) / 2 - cy));
            }
            var (dx, dy) = Delta(i + 1);
            if (dx + dy < 1e-4f) (dx, dy) = Delta(i - 1);
            bool horizontal = dx + dy < 1e-4f ? z.Width >= z.Height : dx >= dy;
            if (horizontal)
            {
                if (cy > 0.5f) bottom++; else top++;
                depthH = Math.Max(depthH, z.Height);
            }
            else
            {
                if (cx > 0.5f) right++; else left++;
                depthV = Math.Max(depthV, z.Width);
            }
        }
        layout.Bottom = bottom; layout.Top = top; layout.Left = left; layout.Right = right;
        if (depthH > 0) layout.DepthHorizontal = depthH;
        if (depthV > 0) layout.DepthVertical = depthV;

        if (layout.Zones.Count >= 2)
        {
            var z0 = layout.Zones[0];
            var z1 = layout.Zones[1];
            float cx0 = (z0.Left + z0.Right) / 2, cy0 = (z0.Top + z0.Bottom) / 2;
            float cx1 = (z1.Left + z1.Right) / 2, cy1 = (z1.Top + z1.Bottom) / 2;
            bool bottomSide = cy0 > 0.5f, rightSide = cx0 > 0.5f;
            layout.StartCorner = (bottomSide, rightSide) switch
            {
                (true, false) => StartCorner.BottomLeft,
                (true, true) => StartCorner.BottomRight,
                (false, false) => StartCorner.TopLeft,
                _ => StartCorner.TopRight,
            };
            // Determine direction from the first step: on the bottom edge going right = counter-clockwise (from the front).
            if (MathF.Abs(cx1 - cx0) >= MathF.Abs(cy1 - cy0))
                layout.Clockwise = bottomSide ? cx1 < cx0 : cx1 > cx0;
            else
                layout.Clockwise = rightSide ? cy1 > cy0 : cy1 < cy0;
        }
    }

    private static int Int(RegistryKey k, string name, int fallback) => k.GetValue(name) is int v ? v : fallback;

    private static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        if (a.Length == 0) return 1;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    // ---------------- switching over ----------------

    public static bool IsRunning() => Process.GetProcessesByName("AmbiBox").Length > 0;

    /// <summary>Closes AmbiBox (so the COM port becomes free). Returns true when no AmbiBox process remains.</summary>
    public static bool Stop()
    {
        foreach (var proc in Process.GetProcessesByName("AmbiBox"))
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(1500)) { proc.Kill(); proc.WaitForExit(2000); }
                Log.Info("Closed AmbiBox");
            }
            catch (Exception ex) { Log.Warn($"Could not close AmbiBox: {ex.Message}"); }
            finally { proc.Dispose(); }
        }
        return !IsRunning();
    }

    private static string StartupShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AmbiBox.lnk");

    public static bool IsInAutostart() => File.Exists(StartupShortcut);

    /// <summary>
    /// Removes AmbiBox from autostart by moving its Startup shortcut to %AppData%\RambiBox\AmbiBox.lnk.disabled
    /// (reversible with <see cref="RestoreAutostart"/>).
    /// </summary>
    public static bool DisableAutostart()
    {
        try
        {
            if (!File.Exists(StartupShortcut)) return true;
            var backup = BackupPath;
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(StartupShortcut, backup, overwrite: true);
            Log.Info($"Moved AmbiBox autostart shortcut to {backup}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not disable AmbiBox autostart: {ex.Message}");
            return false;
        }
    }

    public static bool RestoreAutostart()
    {
        try
        {
            if (!File.Exists(BackupPath)) return false;
            File.Move(BackupPath, StartupShortcut, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    private static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RambiBox", "AmbiBox.lnk.disabled");
}
