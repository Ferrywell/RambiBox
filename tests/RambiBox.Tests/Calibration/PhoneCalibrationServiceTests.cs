using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
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
    internal sealed class FakeSampler : IScreenSampler
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => [CurrentMonitor!];
        public MonitorInfo? CurrentMonitor { get; } = new("m", "Test", @"\\.\DISPLAY1", 0, 0, 2560, 1440, true, false, 60);
        public void Configure(string monitorId, IReadOnlyList<LedZone> zones, bool blackBarDetection) { }
        public SampleResult Sample(Span<float> destRgb, int timeoutMs) => SampleResult.NoChange;
        public bool TryGetPreview(out byte[] bgra, out int width, out int height) { bgra = []; width = height = 0; return false; }
        public (float Top, float Bottom, float Left, float Right) BlackBars => default;
        public void Dispose() { }
    }

    internal sealed class FakeDevice : ILedDevice
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

    private static readonly Lazy<(TempDir Dir, LocalCertificateAuthority Ca)> SharedCa = new(() =>
    {
        var d = new TempDir();
        return (d, new LocalCertificateAuthority(d.Path, "Svc-Test"));
    });

    /// <summary>HttpClient that trusts only the test CA (and accepts its name constraints' IP mismatch for loopback).</summary>
    private static HttpClient TlsClient(LocalCertificateAuthority ca)
    {
        var caCert = ca.Certificate;
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var c2 = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(caCert);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    // Loopback isn't a permitted name; check the signature path and ignore only that status.
                    chain.Build(c2);
                    return chain.ChainStatus.All(s => s.Status is X509ChainStatusFlags.NoError or X509ChainStatusFlags.HasNotPermittedNameConstraint
                        or X509ChainStatusFlags.InvalidNameConstraints);
                },
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    private static PhoneCalibrationService NewService(LightEngine engine, VirtualRig rig, Func<IPEndPoint, IPAddress>? clientIp = null) =>
        new(engine, rig, SharedCa.Value.Ca)
        {
            AddressProvider = () => IPAddress.Loopback,
            AllowTlsForAnyAddress = true,
            ClientAddressOverride = clientIp,
            SettleSecondsOverride = 0,
        };

    private static async Task WaitForTls(PhoneCalibrationService svc)
    {
        for (int i = 0; i < 200 && svc.TlsPort == 0; i++) await Task.Delay(50);
        Assert.NotEqual(0, svc.TlsPort);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r)
    {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Http_landing_tls_ping_profile_live_frames_eye_and_finish()
    {
        using var engine = new LightEngine(new FakeSampler(), new FakeDevice());
        var rig = new VirtualRig { Width = 960, Height = 540 };
        using var svc = NewService(engine, rig);
        int changed = 0;
        svc.Changed += () => Interlocked.Increment(ref changed);

        Assert.True(svc.Start());
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/c/[A-Za-z0-9_-]{22}$", svc.Url!.ToString());
        Assert.Equal(CalibrationPhase.WaitingForPhone, svc.State.Phase);
        Assert.True(svc.Pattern.ShowQr);
        Assert.Same(svc.LedOverride, engine.Override);
        Assert.Equal(OverrideColorPath.WhiteBalanceNoMatrix, engine.Override!.ColorPath);
        await WaitForTls(svc);
        Assert.InRange(svc.TlsPort, PhoneCalibrationService.FirstPort, PhoneCalibrationService.LastPort);
        Assert.NotEqual(svc.Port, svc.TlsPort);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{svc.Port}"), Timeout = TimeSpan.FromSeconds(20) };
        using var https = TlsClient(SharedCa.Value.Ca);
        https.BaseAddress = new Uri($"https://127.0.0.1:{svc.TlsPort}");
        string basePath = svc.Url.AbsolutePath;

        // Wrong token / unknown routes → 404 on both ports
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/c/AAAAAAAAAAAAAAAAAAAAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(basePath + "/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await https.GetAsync("/c/AAAAAAAAAAAAAAAAAAAAAA/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(basePath + "/frame")).StatusCode); // GET on a POST route

        // Landing page (http)
        var page = await http.GetAsync(basePath);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("getUserMedia", html);
        Assert.Contains("capture=\"environment\"", html);
        Assert.Contains("If Brave shows a certificate warning, tap <b>Show details</b> → <b>Visit this website</b>", html);
        Assert.Contains("/skipstep", html);
        Assert.Contains("Skip this step", html);
        Assert.DoesNotContain("exposureCompensation", html); // no exposure fiddling from the page (not available on iOS)
        Assert.Contains("videoWidth", html);
        Assert.Contains($"https://127.0.0.1:{svc.TlsPort}", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(CalibrationPhase.PhoneConnected, svc.State.Phase);
        Assert.False(svc.Pattern.ShowQr);

        // State tells the page where the secure page and the profile are.
        var st = (await JsonOf(await http.GetAsync(basePath + "/state"))).GetProperty("state");
        Assert.Equal("choose", st.GetProperty("mode").GetString());
        Assert.Equal($"https://127.0.0.1:{svc.TlsPort}{basePath}/ping", st.GetProperty("tls").GetProperty("ping").GetString());
        Assert.EndsWith("/rambibox-ca.mobileconfig", st.GetProperty("profileUrl").GetString());

        // Ping over TLS with CORS for the http origin (the phone's landing page)
        Assert.Null(SharedCa.Value.Ca.PhoneTrustedAt);
        var ping = await https.GetAsync(basePath + "/ping");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        Assert.Equal($"http://127.0.0.1:{svc.Port}", ping.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.NotNull(SharedCa.Value.Ca.PhoneTrustedAt);

        // Profile download (iOS)
        var prof = await http.GetAsync(basePath + "/rambibox-ca.mobileconfig");
        Assert.Equal("application/x-apple-aspen-config", prof.Content.Headers.ContentType?.MediaType);
        Assert.Contains("com.apple.security.root", await prof.Content.ReadAsStringAsync());

        // Live mode over https: frames from the virtual rig, until the camera part is done.
        Assert.Equal(HttpStatusCode.OK, (await https.GetAsync(basePath)).StatusCode);
        var live = await JsonOf(await https.PostAsync(basePath + "/live", null));
        var liveState = live.GetProperty("state");
        Assert.Equal("live", liveState.GetProperty("mode").GetString());
        Assert.Equal("ambient", liveState.GetProperty("step").GetString());
        Assert.Equal(8, liveState.GetProperty("stepTimeout").GetDouble());
        Assert.InRange(liveState.GetProperty("screenGain").GetDouble(), ScreenLevels.MinGain, ScreenLevels.MaxGain);
        Assert.Equal(CalibrationPhase.LiveCamera, svc.State.Phase);
        // "Skip this step" on the phone
        var skipped = (await JsonOf(await https.PostAsync(basePath + "/skipstep", null))).GetProperty("state");
        Assert.Equal("white", skipped.GetProperty("step").GetString());
        Assert.Contains("Room light", Assert.Single(skipped.GetProperty("notes").EnumerateArray()).GetString());
        var session = svc.Session!;
        int n = 0;
        string mode = "live";
        while (mode == "live" && n < 90)
        {
            var jpeg = ImageCodec.EncodeJpeg(rig.Shoot(session, svc.LedOverride, n++), 0.9f);
            var content = new ByteArrayContent(jpeg);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var resp = await https.PostAsync(basePath + "/frame", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var j = await JsonOf(resp);
            Assert.True(j.GetProperty("ok").GetBoolean(), j.ToString());
            if (n == 1)
            {
                var ov = j.GetProperty("overlay");
                Assert.Equal(4, ov.GetProperty("corners").GetArrayLength());
                Assert.True(ov.GetProperty("regions").GetArrayLength() >= 8);
                Assert.Equal(960, ov.GetProperty("width").GetInt32());
            }
            mode = j.GetProperty("state").GetProperty("mode").GetString()!;
        }
        Assert.Equal("eye", mode);
        Assert.True(rig.GrayError(0.6) < 3, $"white {rig.GrayError(0.6):0.0}%");
        Assert.Equal(CalibrationPhase.EyeTune, svc.State.Phase);

        // Eye fine-tune (works over http too)
        var eye = await http.PostAsync(basePath + "/eye", new StringContent("{\"action\":\"pad\",\"t\":0.3,\"m\":-0.1}", Encoding.UTF8, "application/json"));
        Assert.True((await JsonOf(eye)).GetProperty("ok").GetBoolean());
        Assert.Equal(0.3, (await JsonOf(await http.GetAsync(basePath + "/state"))).GetProperty("state").GetProperty("eye").GetProperty("t").GetDouble(), 3);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync(basePath + "/eye", new StringContent("{nope"))).StatusCode);
        var wbAfterEye = rig.Wb;

        // Finish
        var fin = await JsonOf(await http.PostAsync(basePath + "/finish", null));
        Assert.True(fin.GetProperty("state").GetProperty("finished").GetBoolean());
        Assert.True(svc.IsFinished);
        Assert.Null(engine.Override);
        svc.Stop(keepResult: true);
        Assert.Equal(wbAfterEye, rig.Wb);
        Assert.Null(svc.Url);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync(basePath + "/state"));
        Assert.True(changed > 10);
    }

    [Fact]
    public async Task Video_batches_are_analyzed_and_cancel_restores_everything()
    {
        using var engine = new LightEngine(new FakeSampler(), new FakeDevice());
        var rig = new VirtualRig { Width = 800, Height = 450, Matrix = [0.9, 0.1, 0, 0, 1, 0, 0, 0, 1] };
        var origMatrix = rig.Matrix;
        using var svc = NewService(engine, rig);
        Assert.True(svc.Start());
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{svc.Port}"), Timeout = TimeSpan.FromSeconds(60) };
        string basePath = svc.Url!.AbsolutePath;
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(basePath)).StatusCode);
        var vs = await JsonOf(await http.PostAsync(basePath + "/video/start", null));
        Assert.Equal("video", vs.GetProperty("state").GetProperty("mode").GetString());
        Assert.Equal(CalibrationPhase.VideoLoop, svc.State.Phase);

        // The service clock is real: film the loop by forcing the session's steps through Tick at the right times would
        // take 17 s, so render each video step directly from a session-equivalent pattern instead.
        var session = svc.Session!;
        var layout = session.Layout;
        var frames = new List<(double T, byte[] Jpeg)>();
        for (int i = 0; i < CalibrationSession.VideoPlan.Length; i++)
        {
            var step = CalibrationSession.VideoPlan[i];
            var pattern = CalibrationSession.VideoPattern(layout, i);
            double l = step.Level;
            var drive = step.Kind == LoopStepKind.Primary
                ? rig.Drive(step.Channel == 0 ? l : 0, step.Channel == 1 ? l : 0, step.Channel == 2 ? l : 0, OverrideColorPath.WhiteBalanceNoMatrix)
                : rig.Drive(l, l, l, OverrideColorPath.WhiteBalanceNoMatrix);
            for (int k = 0; k < 4; k++)
            {
                var img = new SyntheticPhoto
                {
                    Width = 800, Height = 450, Pattern = pattern, GlowCamera = rig.GlowCamera(drive), ScreenMix = rig.ScreenMix,
                    Corners = SyntheticPhoto.Pose(800, 450, VirtualRig.Aspect, 3, -2, 1, 0.6), Seed = i * 10 + k,
                }.Render();
                frames.Add((i * 2.5 + 0.2 + k * 0.5, ImageCodec.EncodeJpeg(img, 0.9f)));
            }
        }
        for (int b = 0; b < frames.Count; b += 7)
        {
            var batch = frames.Skip(b).Take(7).ToList();
            var ms = new MemoryStream();
            ms.Write("RBV1"u8);
            foreach (var (t, jpeg) in batch)
            {
                var hdr = new byte[12];
                BinaryPrimitives.WriteDoubleLittleEndian(hdr, t);
                BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(8), jpeg.Length);
                ms.Write(hdr);
                ms.Write(jpeg);
            }
            var resp = await http.PostAsync(basePath + "/video/frames" + (b == 0 ? "?first=1" : ""), new ByteArrayContent(ms.ToArray()));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var j = await JsonOf(resp);
            Assert.Equal(batch.Count, j.GetProperty("codes").GetArrayLength());
            Assert.All(j.GetProperty("codes").EnumerateArray(), c => Assert.Equal(JsonValueKind.Number, c.ValueKind));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync(basePath + "/video/frames", new ByteArrayContent("XXXX"u8.ToArray()))).StatusCode);
        var done = await JsonOf(await http.PostAsync(basePath + "/video/done", null));
        Assert.True(done.GetProperty("ok").GetBoolean(), done.ToString());
        Assert.Equal("eye", done.GetProperty("state").GetProperty("mode").GetString());
        Assert.True(done.GetProperty("state").GetProperty("hasMatrix").GetBoolean(), done.ToString());
        // Open loop from a 30 % error in one go (through JPEG): within a few percent; the eye step fine-tunes the rest.
        Assert.True(rig.GrayError(0.6) < 4, $"white {rig.GrayError(0.6):0.00}% {done}");

        // Cancel from the phone → the window stops with keepResult false → everything restored.
        await http.PostAsync(basePath + "/cancel", null);
        Assert.True(svc.IsCancelRequested);
        svc.Stop(keepResult: false);
        Assert.Equal(new Rgb3(0.64, 1.0, 0.51), rig.Wb);
        Assert.Equal(new Rgb3(1, 1, 1), rig.Gamma);
        Assert.True(CalibrationSession.SameMatrix(origMatrix, rig.Matrix));
    }

    [Fact]
    public async Task Session_is_bound_to_the_first_client_on_both_ports()
    {
        using var engine = new LightEngine(new FakeSampler(), new FakeDevice());
        var rig = new VirtualRig();
        var phone = IPAddress.Parse("192.168.1.50");
        var sniffer = IPAddress.Parse("192.168.1.66");
        IPAddress current = sniffer;
        using var svc = NewService(engine, rig, _ => Volatile.Read(ref current));
        Assert.True(svc.Start());
        await WaitForTls(svc);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{svc.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        using var https = TlsClient(SharedCa.Value.Ca);
        https.BaseAddress = new Uri($"https://127.0.0.1:{svc.TlsPort}");
        string basePath = svc.Url!.AbsolutePath;

        // Before any page load the API is closed to everyone, on both ports (the token alone is not enough).
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(basePath + "/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await https.GetAsync(basePath + "/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await https.PostAsync(basePath + "/finish", null)).StatusCode);
        Assert.False(svc.IsFinished);

        // The phone loads the page first (http) → bound; it may then use the https port too.
        Volatile.Write(ref current, phone);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(basePath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await https.GetAsync(basePath + "/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await https.GetAsync(basePath)).StatusCode);

        // A sniffer with the token: refused on both ports, nothing changes.
        Volatile.Write(ref current, sniffer);
        foreach (var c in new[] { http, https })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(basePath)).StatusCode);
            var st = await c.GetAsync(basePath + "/state");
            Assert.Equal(HttpStatusCode.Forbidden, st.StatusCode);
            Assert.False((await JsonOf(st)).TryGetProperty("state", out _)); // no state leaked
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync(basePath + "/frame", new ByteArrayContent(new byte[100]))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync(basePath + "/skipstep", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync(basePath + "/eye", new StringContent("{\"action\":\"reset\"}"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync(basePath + "/finish", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync(basePath + "/cancel", null)).StatusCode);
        }
        Assert.False(svc.IsFinished);
        Assert.False(svc.IsCancelRequested);
        Assert.Equal(0, rig.Applies);

        // Restarting issues a new token and a fresh binding.
        svc.Stop(keepResult: true);
        Assert.True(svc.Start());
        using var http2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{svc.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        Assert.Equal(HttpStatusCode.NotFound, (await http2.GetAsync(basePath)).StatusCode); // old token
        Assert.Equal(HttpStatusCode.OK, (await http2.GetAsync(svc.Url!.AbsolutePath)).StatusCode);
    }

    [Fact]
    public async Task Per_ip_cap_applies_on_the_tls_port()
    {
        using var engine = new LightEngine(new FakeSampler(), new FakeDevice());
        using var svc = NewService(engine, new VirtualRig());
        Assert.True(svc.Start());
        await WaitForTls(svc);
        var idle = new List<System.Net.Sockets.TcpClient>();
        try
        {
            for (int i = 0; i < MiniHttpServer.MaxPerClient; i++)
            {
                var c = new System.Net.Sockets.TcpClient();
                c.Connect(IPAddress.Loopback, svc.TlsPort); // never starts the TLS handshake
                idle.Add(c);
            }
            await Task.Delay(300);
            using var https = TlsClient(SharedCa.Value.Ca);
            // The 5th connection from the same IP is closed at once (no plain-text 503 possible on a TLS port).
            await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                https.GetAsync($"https://127.0.0.1:{svc.TlsPort}{svc.Url!.AbsolutePath}/ping"));
        }
        finally
        {
            foreach (var c in idle) c.Dispose();
        }
    }

    [Fact]
    public void Start_fails_cleanly_without_network()
    {
        using var engine = new LightEngine(new FakeSampler(), new FakeDevice());
        using var svc = new PhoneCalibrationService(engine, new VirtualRig()) { AddressProvider = () => null };
        Assert.False(svc.Start());
        Assert.Equal(CalibrationPhase.Failed, svc.State.Phase);
        Assert.Null(svc.Url);
        Assert.Null(engine.Override);
    }

    [Fact]
    public void Video_batch_parser_validates_input()
    {
        var ok = new byte[] { (byte)'R', (byte)'B', (byte)'V', (byte)'1' }.Concat(BitConverter.GetBytes(1.5)).Concat(BitConverter.GetBytes(3)).Concat(new byte[] { 1, 2, 3 }).ToArray();
        var f = Assert.Single(PhoneCalibrationService.ParseVideoBatch(ok));
        Assert.Equal(1.5, f.T);
        Assert.Equal([1, 2, 3], f.Jpeg);
        Assert.Throws<FormatException>(() => PhoneCalibrationService.ParseVideoBatch(ok[..^1]));
        Assert.Throws<FormatException>(() => PhoneCalibrationService.ParseVideoBatch("RBV2"u8.ToArray()));
        var neg = (byte[])ok.Clone();
        BitConverter.GetBytes(-1).CopyTo(neg, 12);
        Assert.Throws<FormatException>(() => PhoneCalibrationService.ParseVideoBatch(neg));
    }

    [Fact]
    public void Eye_command_parser()
    {
        var c = PhoneCalibrationService.ParseEye("{\"action\":\"stage\",\"stage\":\"dark\"}"u8.ToArray());
        Assert.Equal("stage", c.Action);
        Assert.Equal(EyeStage.Dark, c.Stage);
        c = PhoneCalibrationService.ParseEye("{\"action\":\"vote\",\"variant\":\"A\",\"color\":2}"u8.ToArray());
        Assert.Equal(("A", 2), (c.Variant, c.Color));
        Assert.Throws<FormatException>(() => PhoneCalibrationService.ParseEye("{\"action\":\"stage\",\"stage\":\"x\"}"u8.ToArray()));
    }

    [Fact]
    public void Pattern_has_markers_bands_code_and_reference_inside_the_screen()
    {
        foreach (double aspect in new[] { 16.0 / 9.0, 4.0 / 3.0, 21.0 / 9.0 })
        {
            var layout = new CalibrationLayout(aspect);
            var lv = ScreenLevels.ForGain(0.3);
            var p = layout.BuildMeasurePattern(lv, 21, showQr: false);
            Assert.All(p.Rects, r => Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= 1 && r.Bottom <= 1 && r.Left < r.Right && r.Top < r.Bottom));
            Assert.Equal(lv.RefRgb(0), lv.BandRgb); // the 1× reference square shows exactly the band level
            Assert.Equal(5, p.Rects.Count(r => r.Rgb == lv.BandRgb));
            for (int i = 0; i < CalibrationLayout.RefCount; i++)
                Assert.Single(p.Rects, r => r.Rgb == lv.RefRgb(i) && Math.Abs((r.Left + r.Right) / 2 - layout.RefPatch(i).Center.X) < 1e-6);
            Assert.Equal(21, p.Code);
            var probe = layout.BlackProbe();
            // nothing drawn inside the black probe
            Assert.DoesNotContain(p.Rects, r => r.Left < probe.R && r.Right > probe.L && r.Top < probe.B && r.Bottom > probe.T);
            // reference squares, code cells and bands don't overlap each other
            var boxes = Enumerable.Range(0, CalibrationLayout.RefCount).Select(layout.RefPatch)
                .Concat(Enumerable.Range(0, StepCode.Cells).Select(layout.CodeCell))
                .Concat(Enum.GetValues<Side>().Select(layout.Band)).ToList();
            for (int a = 0; a < boxes.Count; a++)
                for (int b = a + 1; b < boxes.Count; b++)
                    Assert.False(boxes[a].L < boxes[b].R && boxes[a].R > boxes[b].L && boxes[a].T < boxes[b].B && boxes[a].B > boxes[b].T, $"{aspect}: {a} overlaps {b}");
            // the orientation cue's mirror images are dark (black probe / empty area)
            foreach (var c in layout.OrientationCuePoints())
                Assert.DoesNotContain(p.Rects, r => r.Rgb != 0 && r.Left <= 1 - c.X && r.Right >= 1 - c.X && r.Top <= 1 - c.Y && r.Bottom >= 1 - c.Y);
        }
        {
            var layout = new CalibrationLayout(16.0 / 9.0);
            var q = layout.BuildMeasurePattern(CalibrationLayout.WhiteBandRgb, 21, showQr: true);
            Assert.Equal(12, q.Rects.Count);
            Assert.Equal((q.QrRight - q.QrLeft) * 16.0 / 9.0, q.QrBottom - q.QrTop, 4);
            var eye = layout.BuildEyePattern(0xFFFFFF);
            Assert.True(eye.EyeMode);
            Assert.Equal(4, eye.Rects.Count);
            Assert.Equal(CalibrationLayout.WhiteBandRgb, CalibrationLayout.GrayBandRgb(0.6));
            Assert.Equal(0xB30000, CalibrationLayout.PrimaryBandRgb(0));
            Assert.Equal(0x0000B3, CalibrationLayout.PrimaryBandRgb(2));
        }
    }

    [Fact]
    public void Screen_levels_follow_the_gain_with_exact_codes()
    {
        var lv = ScreenLevels.ForGain(0.2);
        Assert.Equal(0.2, CalibrationAnalyzer.ToLinear((byte)(lv.BandRgb & 0xFF)), 2);
        for (int i = 0; i < CalibrationLayout.RefCount; i++)
            Assert.Equal(0.2 * CalibrationLayout.RefFactors[i], lv.RefLinear(i), 2);
        Assert.Equal(0.8, CalibrationAnalyzer.ToLinear((byte)(lv.MarkerRgb & 0xFF)), 2); // markers 4× the band
        Assert.Equal(0xFFFFFF, ScreenLevels.ForGain(0.8).MarkerRgb);                     // capped at white
        Assert.Equal(ScreenLevels.MarkerFloor, CalibrationAnalyzer.ToLinear((byte)(ScreenLevels.ForGain(0.01).MarkerRgb & 0xFF)), 2);
        var red = ScreenLevels.ForGain(0.2, 0);
        Assert.Equal(lv.BandRgb & 0xFF0000, red.BandRgb);
        Assert.Equal(ScreenLevels.MinGain, ScreenLevels.ForGain(0.0001).Gain);
        Assert.Equal(ScreenLevels.MaxGain, ScreenLevels.ForGain(7).Gain);
        Assert.True(ScreenLevels.ForGain(0.2).SameAs(ScreenLevels.ForGain(0.2001)));
        Assert.False(ScreenLevels.ForGain(0.2).SameAs(ScreenLevels.ForGain(0.25)));
        Assert.All(ScreenLevels.ForGain(ScreenLevels.MinGain).RefCodes, c => Assert.True(c >= 1));
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
