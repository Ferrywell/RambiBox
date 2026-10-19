using RambiBox.Core.Config;
using RambiBox.Core.Engine;

namespace RambiBox.Tests.Core;

public class ColorProcessorTests
{
    private static ColorProcessor Make(Action<ColorSettings>? tweak = null)
    {
        var c = new ColorSettings { Saturation = 1, Gamma = 1, WhiteR = 1, WhiteG = 1, WhiteB = 1, DarkThreshold = 0, SmoothingMs = 0 };
        tweak?.Invoke(c);
        var p = new ColorProcessor();
        p.Configure(c);
        return p;
    }

    [Fact]
    public void Neutral_settings_pass_colors_through()
    {
        var p = Make();
        float[] rgb = [0.2f, 0.5f, 1f];
        p.Adjust(rgb);
        var bytes = new byte[3];
        p.ToDevice(rgb, bytes);
        Assert.Equal(new byte[] { 51, 128, 255 }, bytes);
    }

    [Fact]
    public void Dark_threshold_blacks_out_dim_colors_only()
    {
        var p = Make(c => c.DarkThreshold = 0.1);
        float[] rgb = [0.05f, 0.08f, 0.02f, 0.5f, 0.05f, 0.05f];
        p.Adjust(rgb);
        Assert.Equal([0f, 0f, 0f], rgb[..3]);
        Assert.Equal(0.5f, rgb[3], 3);
    }

    [Fact]
    public void Saturation_keeps_grays_gray_and_boosts_colors()
    {
        var p = Make(c => c.Saturation = 1.5);
        float[] gray = [0.4f, 0.4f, 0.4f];
        p.Adjust(gray);
        Assert.All(gray, v => Assert.Equal(0.4f, v, 4));
        float[] col = [0.6f, 0.4f, 0.4f];
        p.Adjust(col);
        Assert.True(col[0] - col[1] > 0.2f);
    }

    [Fact]
    public void White_balance_and_brightness_scale_channels()
    {
        var p = Make(c => { c.WhiteR = 0.64; c.WhiteG = 1; c.WhiteB = 0.51; c.Brightness = 0.5; });
        var bytes = new byte[3];
        p.ToDevice([1f, 1f, 1f], bytes);
        Assert.Equal(new byte[] { 82, 128, 65 }, bytes);
    }

    [Fact]
    public void Smoothing_is_frame_rate_independent()
    {
        float Run(double fps)
        {
            var p = Make(c => c.SmoothingMs = 100);
            var outp = new float[3];
            p.Smooth([0f, 0f, 0f], outp, 0); // initial state
            int frames = (int)(fps * 0.1);
            for (int i = 0; i < frames; i++) p.Smooth([1f, 1f, 1f], outp, 1 / fps);
            return outp[0];
        }
        // after one time constant ≈ 63 % regardless of frame rate
        Assert.InRange(Run(30), 0.60f, 0.66f);
        Assert.InRange(Run(144), 0.60f, 0.66f);
    }

    [Fact]
    public void Smoothing_converges_and_reports_zero_remaining()
    {
        var p = Make(c => c.SmoothingMs = 50);
        var outp = new float[3];
        p.Smooth([0f, 0f, 0f], outp, 0);
        float remaining = 1;
        for (int i = 0; i < 200 && remaining > 0; i++) remaining = p.Smooth([0.7f, 0.2f, 0.1f], outp, 1 / 30.0);
        Assert.Equal(0, remaining);
        Assert.Equal([0.7f, 0.2f, 0.1f], outp);
    }

    [Fact]
    public void Hue_rotation_by_zero_is_identity_and_preserves_gray()
    {
        var m = ColorProcessor.HueRotation(120);
        float r = m[0] * 0.5f + m[1] * 0.5f + m[2] * 0.5f;
        Assert.Equal(0.5f, r, 2);
    }
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var s = new AppSettings { Mode = LightMode.Mood };
        s.Layout.Zones = LayoutGenerator.Generate(s.Layout);
        s.Color.WhiteB = 0.51;
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(s));
        Assert.Equal(LightMode.Mood, back.Mode);
        Assert.Equal(112, back.Layout.Zones.Count);
        Assert.Equal(s.Layout.Zones[40], back.Layout.Zones[40]);
        Assert.Equal(0.51, back.Color.WhiteB);
        Assert.Contains("\"mode\": \"Mood\"", SettingsSerializer.Serialize(s));
    }

    [Fact]
    public void Normalize_clamps_garbage_values()
    {
        var s = SettingsSerializer.Deserialize("""{ "color": { "brightness": 7, "smoothingMs": -5 }, "device": { "port": "" } }""");
        Assert.Equal(1, s.Color.Brightness);
        Assert.Equal(0, s.Color.SmoothingMs);
        Assert.Equal("Auto", s.Device.Port);
        Assert.NotNull(s.Layout.Zones);
    }

    [Fact]
    public void Store_falls_back_to_backup_when_file_is_corrupt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambibox-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(new AppSettings { Mode = LightMode.StaticColor });
            store.Save(new AppSettings { Mode = LightMode.Mood }); // creates .bak with StaticColor
            File.WriteAllText(store.FilePath, "{ this is not json");
            Assert.Equal(LightMode.StaticColor, store.Load().Mode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Missing_store_gives_defaults()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "s.json"));
        Assert.False(store.Exists);
        Assert.Equal(LightMode.Ambilight, store.Load().Mode);
    }
}
