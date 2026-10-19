using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RambiBox.Core.Calibration;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;
using RambiBox.Core.Engine;

namespace RambiBox.Tests.Calibration;

public class PhoneCalibrationServiceTests
{
    private sealed class FakeSampler : IScreenSampler
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [CurrentMonitor!];
        public MonitorInfo? CurrentMonitor { get; } = new("m", "Test", @"\\.\DISPLAY1", 0, 0, 2560, 1440, true, false, 60);
        public void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection) { }
        public SampleResult Sample(Span<float> destRgb, int timeoutMs) => SampleResult.NoChange;
        public bool TryGetPreview(out byte[] bgra, out int width, out int height) { bgra = []; width = height = 0; return false; }
        public (float Top, float Bottom, float Left, float Right) BlackBars => default;
        public void Dispose() { }
    }

    private sealed class FakeDevice : ILedDevice
    {
        public DeviceStatus Status => DeviceStatus.Stopped;
        public string? PortName => null;
        public event Action? StatusChanged { add { } remove { } }
        public event Action<SerialPortInfo>? DeviceFound { add { } remove { } }
        public void Configure(DeviceSettings settings, int ledCount) { }
        public void Submit(ReadOnlySpan<byte> rgb) { }
        public bool WaitForSlot(int timeoutMs) => false;
        public double MaxFrameRate => 0;
        public double FramesPerSecond => 0;
        public void Rescan() { }
        public void Blackout() { }
        public IReadOnlyList<SerialPortInfo> ListPorts() => [];
        public void Dispose() { }
    }

    [Fact]
    public async Task Http_round_trip()
    {
        using var engine = new LightEngine(new FakeSampler(), new FakeDevice());
        var applied = new List<(double, double, double)>();
        var gammaApplied = new List<(double, double, double)>();
        using var svc = new PhoneCalibrationService(engine, () => (0.64, 1.0, 0.51),
            (r, g, b) => { lock (applied) applied.Add((r, g, b)); },
            (r, g, b) => { lock (gammaApplied) gammaApplied.Add((r, g, b)); },
            () => (1, 1, 1))
        {
            AddressProvider = () => IPAddress.Loopback,
        };
        int changed = 0;
        svc.Changed += () => Interlocked.Increment(ref changed);

        Assert.True(svc.Start());
        Assert.NotNull(svc.Url);
        Assert.InRange(svc.Port, PhoneCalibrationService.FirstPort, PhoneCalibrationService.LastPort);
        Assert.Equal(CalibrationPhase.WaitingForPhone, svc.State.Phase);
        Assert.True(svc.Pattern.ShowQr);
        Assert.NotNull(engine.Override);
        Assert.True(engine.Override!.ApplyWhiteBalance);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/c/[A-Za-z0-9_-]{22}$", svc.Url!.ToString());

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{svc.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        string basePath = svc.Url.AbsolutePath;

        // Wrong token / unknown routes → 404
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/c/AAAAAAAAAAAAAAAAAAAAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(basePath + "/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/c/wrong/photo", new ByteArrayContent(new byte[100]))).StatusCode);
        Assert.Equal(CalibrationPhase.WaitingForPhone, svc.State.Phase);

        // Page
        var page = await http.GetAsync(basePath);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Take photo", html);
        Assert.Contains("capture=\"environment\"", html);
        Assert.DoesNotContain("https://", html); // self-contained, no CDNs
        Assert.Equal(CalibrationPhase.WaitingForPhoto, svc.State.Phase);
        Assert.False(svc.Pattern.ShowQr);
        Assert.True(changed >= 2);

        // State
        using (var st = JsonDocument.Parse(await http.GetStringAsync(basePath + "/state")))
        {
            Assert.Equal("WaitingForPhoto", st.RootElement.GetProperty("phase").GetString());
            Assert.Equal(0.64, st.RootElement.GetProperty("whiteR").GetDouble(), 3);
            Assert.False(st.RootElement.GetProperty("finished").GetBoolean());
        }

        // Photo upload (layout aspect comes from the fake monitor: 2560x1440 = 16:9)
        var tint = new Rgb3(1.05, 0.9, 1.45);
        var jpeg = new SyntheticPhoto { Wb = new Rgb3(0.64, 1.0, 0.51), Tint = tint, Seed = 99 }.RenderJpeg();
        var content = new ByteArrayContent(jpeg);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var resp = await http.PostAsync(basePath + "/photo", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using (var r = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
        {
            var root = r.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean(), root.GetProperty("message").GetString());
            Assert.Equal(4, root.GetProperty("corners").GetArrayLength());
            Assert.Equal(8, root.GetProperty("sampleRegions").GetArrayLength());
            Assert.True(root.GetProperty("measuredErrorPercent").GetDouble() > 5);
            Assert.Equal("Adjusted", root.GetProperty("phase").GetString());
            Assert.Equal(1, root.GetProperty("photosAnalyzed").GetInt32());
        }
        Assert.Single(applied);
        Assert.Equal(CalibrationPhase.Adjusted, svc.State.Phase);
        Assert.Equal(1, svc.State.PhotosAnalyzed);
        Assert.Equal(1.0, Math.Max(svc.State.WhiteR, Math.Max(svc.State.WhiteG, svc.State.WhiteB)), 6);

        // Re-analyze with user corners (from the same photo)
        var corners = SyntheticPhoto.Pose(SyntheticPhoto.DefaultWidth, SyntheticPhoto.DefaultHeight, 16.0 / 9.0);
        var body = JsonSerializer.Serialize(new { corners = corners.Select(c => new[] { c.X, c.Y }).ToArray() });
        resp = await http.PostAsync(basePath + "/corners", new StringContent(body, Encoding.UTF8, "application/json"));
        using (var r = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
        {
            Assert.True(r.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, r.RootElement.GetProperty("photosAnalyzed").GetInt32()); // same photo isn't counted twice
        }
        Assert.Equal(2, applied.Count);
        Assert.Equal(applied[0].Item1, applied[1].Item1, 2); // same base WB → same correction

        // Garbage upload is rejected politely
        resp = await http.PostAsync(basePath + "/photo", new ByteArrayContent(Encoding.ASCII.GetBytes("definitely not a jpeg file")));
        using (var r = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()))
            Assert.False(r.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(CalibrationPhase.PhotoRejected, svc.State.Phase);

        // Dark stage
        resp = await http.PostAsync(basePath + "/dark", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(svc.IsDarkStage);
        Assert.Equal((float)CalibrationMath.DarkLevel, svc.LedLevel);
        Assert.Contains(svc.Pattern.Rects, r => r.Rgb == CalibrationLayout.DarkBandRgb);

        // Finish
        resp = await http.PostAsync(basePath + "/finish", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(svc.IsFinished);
        Assert.Null(engine.Override);
        Assert.Equal(CalibrationPhase.Converged, svc.State.Phase);

        // Cancel → original white balance restored
        svc.Stop(keepResult: false);
        Assert.Null(svc.Url);
        Assert.Equal((0.64, 1.0, 0.51), applied[^1]);
        Assert.Empty(gammaApplied); // gamma never changed → nothing to restore
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(basePath + "/state"));
    }

    [Fact]
    public void Start_fails_cleanly_without_network()
    {
        using var engine = new LightEngine(new FakeSampler(), new FakeDevice());
        using var svc = new PhoneCalibrationService(engine, () => (1, 1, 1), (_, _, _) => { }) { AddressProvider = () => null };
        Assert.False(svc.Start());
        Assert.Equal(CalibrationPhase.Failed, svc.State.Phase);
        Assert.Null(svc.Url);
        Assert.Null(engine.Override);
    }

    [Fact]
    public void Pattern_has_markers_and_bands_inside_the_screen()
    {
        var layout = new CalibrationLayout(16.0 / 9.0);
        var p = layout.BuildPattern(showQr: true, darkStage: false);
        Assert.Equal(12, p.Rects.Count);
        Assert.All(p.Rects, r => Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= 1 && r.Bottom <= 1 && r.Left < r.Right && r.Top < r.Bottom));
        Assert.Equal(4, p.Rects.Count(r => r.Rgb == CalibrationLayout.WhiteBandRgb));
        // QR area is square on a 16:9 monitor
        Assert.Equal((p.QrRight - p.QrLeft) * 16.0 / 9.0, p.QrBottom - p.QrTop, 4);
    }

    [Fact]
    public void Client_address_filter()
    {
        Assert.True(LanAddress.IsAllowedClient(IPAddress.Parse("192.168.1.20")));
        Assert.True(LanAddress.IsAllowedClient(IPAddress.Parse("10.1.2.3")));
        Assert.True(LanAddress.IsAllowedClient(IPAddress.Parse("172.20.0.5")));
        Assert.True(LanAddress.IsAllowedClient(IPAddress.Loopback));
        Assert.False(LanAddress.IsAllowedClient(IPAddress.Parse("172.32.0.5")));
        Assert.False(LanAddress.IsAllowedClient(IPAddress.Parse("8.8.8.8")));
    }
}
