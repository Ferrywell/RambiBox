using RambiBox.Core.Config;

namespace RambiBox.Tests.Core;

public class CaptureMethodSettingsTests
{
    [Fact]
    public void Defaults_to_auto()
    {
        Assert.Equal(CaptureMethod.Auto, new AppSettings().Capture.Method);
    }

    [Fact]
    public void Settings_files_from_1_0_0_without_the_method_load_as_auto()
    {
        var s = SettingsSerializer.Deserialize("""
            { "schemaVersion": 1, "capture": { "monitorId": "\\\\?\\DISPLAY#AUS276B", "maxFps": 30, "blackBarDetection": false } }
            """);
        Assert.Equal(CaptureMethod.Auto, s.Capture.Method);
        Assert.Equal(@"\\?\DISPLAY#AUS276B", s.Capture.MonitorId);
        Assert.Equal(30, s.Capture.MaxFps);
        Assert.False(s.Capture.BlackBarDetection);
    }

    [Theory]
    [InlineData(CaptureMethod.Auto)]
    [InlineData(CaptureMethod.WindowsGraphicsCapture)]
    [InlineData(CaptureMethod.DesktopDuplication)]
    public void Round_trips_as_a_readable_name(CaptureMethod m)
    {
        var s = new AppSettings();
        s.Capture.Method = m;
        var json = SettingsSerializer.Serialize(s);
        Assert.Contains($"\"method\": \"{m}\"", json);
        Assert.Equal(m, SettingsSerializer.Deserialize(json).Capture.Method);
        Assert.Equal(m, s.Clone().Capture.Method);
    }

    [Theory]
    [InlineData("\"desktopduplication\"", CaptureMethod.DesktopDuplication)] // case-insensitive
    [InlineData("\"FutureMethod\"", CaptureMethod.Auto)]                     // written by a newer version
    [InlineData("\"\"", CaptureMethod.Auto)]
    [InlineData("\"7\"", CaptureMethod.Auto)]
    [InlineData("2", CaptureMethod.DesktopDuplication)]
    [InlineData("99", CaptureMethod.Auto)]
    [InlineData("-1", CaptureMethod.Auto)]
    [InlineData("null", CaptureMethod.Auto)]
    [InlineData("true", CaptureMethod.Auto)]
    [InlineData("{ \"x\": 1 }", CaptureMethod.Auto)]
    public void Unknown_values_become_auto_without_losing_the_other_settings(string value, CaptureMethod expected)
    {
        var s = SettingsSerializer.Deserialize($$"""
            { "color": { "brightness": 0.5 }, "capture": { "method": {{value}}, "maxFps": 25 } }
            """);
        Assert.Equal(expected, s.Capture.Method);
        Assert.Equal(0.5, s.Color.Brightness);
        Assert.Equal(25, s.Capture.MaxFps);
    }

    [Fact]
    public void Normalize_repairs_an_undefined_value()
    {
        var s = new AppSettings();
        s.Capture.Method = (CaptureMethod)42;
        SettingsSerializer.Normalize(s);
        Assert.Equal(CaptureMethod.Auto, s.Capture.Method);
    }
}
