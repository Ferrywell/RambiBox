using System.Diagnostics;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Core.Engine;

/// <summary>
/// The heart of the app: one dedicated thread that captures → processes → sends, paced by the LED link
/// so a frame is captured "just in time" (lowest possible latency) and nothing piles up in buffers.
/// When the picture is static and smoothing has converged, the loop blocks inside the capture call,
/// so idle CPU usage is ~0.
/// </summary>
public sealed class LightEngine : IDisposable
{
    private readonly IScreenSampler _sampler;
    private readonly ILedDevice _device;
    private readonly ColorProcessor _proc = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _previewLock = new();

    private Thread? _thread;
    private volatile bool _running;
    private volatile AppSettings? _pendingSettings;
    private volatile ILedOverride? _override;
    private int _paused; // PauseReason flags (Interlocked)

    private AppSettings _settings = new();
    private byte[] _preview = Array.Empty<byte>();
    private EngineStats _stats;

    public LightEngine(IScreenSampler sampler, ILedDevice device)
    {
        _sampler = sampler;
        _device = device;
        _stats = new EngineStats(0, 0, 0, 0, DeviceStatus.Stopped, null, 0, null, PauseReason.None, false, "Starting…");
        _device.StatusChanged += () => _wake.Set();
    }

    public IScreenSampler Sampler => _sampler;
    public ILedDevice Device => _device;

    /// <summary>Number of LEDs in the current layout (excluding offset LEDs).</summary>
    public int LedCount => _settings.Layout.Zones.Count;

    public PauseReason Paused => (PauseReason)Volatile.Read(ref _paused);

    public void Start(AppSettings initial)
    {
        if (_running) return;
        _settings = initial.Clone();
        _pendingSettings = null;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "RambiBox engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Thread-safe; takes a snapshot. Applied at the start of the next engine iteration.</summary>
    public void UpdateSettings(AppSettings s)
    {
        _pendingSettings = s.Clone();
        _wake.Set();
    }

    public void SetPaused(PauseReason reason, bool paused)
    {
        int old, nw;
        do
        {
            old = Volatile.Read(ref _paused);
            nw = paused ? old | (int)reason : old & ~(int)reason;
        } while (Interlocked.CompareExchange(ref _paused, nw, old) != old);
        if (old != nw) Log.Info($"Pause state: {(PauseReason)nw}");
        _wake.Set();
    }

    /// <summary>Show an override (test pattern / calibration) instead of the normal output; null restores.</summary>
    public void SetOverride(ILedOverride? o)
    {
        _override = o;
        _wake.Set();
    }

    public ILedOverride? Override => _override;

    public EngineStats GetStats() => Volatile.Read(ref _stats);

    /// <summary>Copies the colors the LEDs currently aim to show (before white balance), R,G,B bytes per zone.</summary>
    public byte[] GetPreviewColors()
    {
        lock (_previewLock) return (byte[])_preview.Clone();
    }

    private void Run()
    {
        try { Loop(); }
        catch (Exception ex) { Log.Error("Engine thread crashed", ex); }
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        float[] raw = Array.Empty<float>(), target = Array.Empty<float>(), output = Array.Empty<float>();
        byte[] frame = Array.Empty<byte>(), lastSent = Array.Empty<byte>();
        double lastIter = 0, lastSendTime = -10;
        bool haveCapture = false;
        int ledCount = -1, offset = 0;
        LightMode lastMode = (LightMode)(-1);

        // Stats (exponential moving averages)
        double outFps = 0, capFps = 0, capMs = 0, procMs = 0;
        double lastStatsPublish = 0;
        int sentCount = 0, capCount = 0;
        double statWindowStart = 0;

        ApplySettings(_settings, initial: true);

        while (_running)
        {
            if (_pendingSettings is { } ns)
            {
                _pendingSettings = null;
                ApplySettings(ns, initial: false);
            }
            var s = _settings;

            int n = s.Layout.Zones.Count;
            if (n != ledCount || s.Layout.Offset != offset)
            {
                ledCount = n;
                offset = s.Layout.Offset;
                raw = new float[n * 3];
                target = new float[n * 3];
                output = new float[n * 3];
                frame = new byte[(n + offset) * 3];
                lastSent = Array.Empty<byte>();
                haveCapture = false;
                _proc.Reset();
            }

            var paused = Paused;
            if (!s.Enabled) paused |= PauseReason.UserOff;
            var ov = _override;
            double now = clock.Elapsed.TotalSeconds;

            // ---- Off / paused: send black (a keep-alive once per second), then sleep until woken.
            if (paused != PauseReason.None && ov is null)
            {
                Array.Clear(frame);
                if (!frame.AsSpan().SequenceEqual(lastSent) || now - lastSendTime > 1.0)
                {
                    if (_device.Status == DeviceStatus.Connected && _device.WaitForSlot(100))
                    {
                        _device.Submit(frame);
                        lastSent = (byte[])frame.Clone();
                        lastSendTime = now;
                    }
                }
                lock (_previewLock) _preview = new byte[n * 3];
                _proc.Reset();
                haveCapture = false;
                PublishStats(0, 0, capMs, procMs, paused, ov, force: now - lastStatsPublish > 0.5);
                lastStatsPublish = now;
                _wake.WaitOne(250);
                lastIter = clock.Elapsed.TotalSeconds;
                continue;
            }

            // ---- Not connected: don't waste CPU on capturing.
            if (_device.Status != DeviceStatus.Connected)
            {
                PublishStats(0, 0, capMs, procMs, paused, ov, force: now - lastStatsPublish > 0.5);
                lastStatsPublish = now;
                _wake.WaitOne(250);
                lastSent = Array.Empty<byte>(); // resend on reconnect
                lastIter = clock.Elapsed.TotalSeconds;
                continue;
            }

            // ---- Pace: wait for the link to be free (so the capture below is as fresh as possible).
            if (!_device.WaitForSlot(200)) continue;
            if (s.Capture.MaxFps > 0)
            {
                double minInterval = 1.0 / s.Capture.MaxFps;
                double wait = lastSendTime + minInterval - clock.Elapsed.TotalSeconds;
                if (wait > 0.001) _wake.WaitOne(TimeSpan.FromSeconds(wait));
            }

            now = clock.Elapsed.TotalSeconds;
            double dt = lastIter == 0 ? 0 : now - lastIter;
            lastIter = now;
            var t0 = Stopwatch.GetTimestamp();
            bool whiteBalanceOnly = false, raw01 = false;
            float remaining = 0;

            if (ov is not null)
            {
                ov.Render(output, n, now);
                raw01 = !ov.ApplyWhiteBalance;
                whiteBalanceOnly = ov.ApplyWhiteBalance;
                _proc.Reset();
                haveCapture = false;
            }
            else
            {
                if (s.Mode != lastMode) { lastMode = s.Mode; haveCapture = false; }
                switch (s.Mode)
                {
                    case LightMode.Ambilight:
                    {
                        // When nothing is moving (converged + no new frames) block a while inside capture to idle cheaply.
                        bool idle = haveCapture && remainingLast < 0.0001f && now - lastSendTime < 0.9;
                        var c0 = Stopwatch.GetTimestamp();
                        var r = _sampler.Sample(raw, idle ? 100 : 0);
                        if (r == SampleResult.NewFrame)
                        {
                            double ms = Stopwatch.GetElapsedTime(c0).TotalMilliseconds;
                            capMs = capMs == 0 ? ms : capMs * 0.9 + ms * 0.1;
                            capCount++;
                            raw.CopyTo(target, 0);
                            _proc.Adjust(target);
                            haveCapture = true;
                        }
                        else if (!haveCapture)
                        {
                            // Nothing captured yet (startup / secure desktop): keep LEDs dark rather than garbage.
                            Array.Clear(target);
                        }
                        remaining = _proc.Smooth(target, output, dt);
                        break;
                    }
                    case LightMode.StaticColor:
                    {
                        int c = s.Static.Color;
                        float r = ((c >> 16) & 0xFF) / 255f, g = ((c >> 8) & 0xFF) / 255f, b = (c & 0xFF) / 255f;
                        for (int i = 0; i < n; i++) { target[3 * i] = r; target[3 * i + 1] = g; target[3 * i + 2] = b; }
                        remaining = _proc.Smooth(target, output, dt);
                        break;
                    }
                    case LightMode.Mood:
                    {
                        RenderMood(s.Static, target, n, now);
                        remaining = _proc.Smooth(target, output, dt);
                        // Animations always change; never idle-block.
                        remaining = Math.Max(remaining, 1f);
                        break;
                    }
                }
            }
            remainingLast = remaining;

            // ---- Convert & send
            var dest = frame.AsSpan(offset * 3);
            frame.AsSpan(0, offset * 3).Clear();
            if (raw01) ColorProcessor.Raw(output, dest);
            else _proc.ToDevice(output, dest, whiteBalanceOnly);

            double pms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            procMs = procMs == 0 ? pms : procMs * 0.9 + pms * 0.1;

            bool changed = !frame.AsSpan().SequenceEqual(lastSent);
            if (changed || now - lastSendTime > 1.0) // keep-alive so firmware timeouts never blank the strip
            {
                _device.Submit(frame);
                if (lastSent.Length != frame.Length) lastSent = new byte[frame.Length];
                frame.CopyTo(lastSent, 0);
                lastSendTime = now;
                sentCount++;
            }
            else if (ov is null && s.Mode == LightMode.Ambilight && haveCapture)
            {
                // Static picture: nothing to send. Block in capture next iteration (handled by 'idle').
            }

            // Preview (pre white balance), cheap
            var pv = new byte[n * 3];
            ColorProcessor.Raw(output, pv);
            lock (_previewLock) _preview = pv;

            if (now - statWindowStart >= 1.0)
            {
                double win = now - statWindowStart;
                outFps = sentCount / win;
                capFps = capCount / win;
                sentCount = capCount = 0;
                statWindowStart = now;
                PublishStats(outFps, capFps, capMs, procMs, paused, ov, force: true);
                lastStatsPublish = now;
            }
        }
    }

    // Kept as a field so the idle decision can see the previous iteration's result.
    private float remainingLast = 1f;

    private void ApplySettings(AppSettings s, bool initial)
    {
        SettingsSerializer.Normalize(s);
        _settings = s;
        _proc.Configure(s.Color);
        _sampler.Configure(s.Capture.MonitorId, s.Layout.Zones, s.Capture.BlackBarDetection);
        _device.Configure(s.Device, s.Layout.Zones.Count + s.Layout.Offset);
        if (initial) Log.Info($"Engine started: {s.Layout.Zones.Count} LEDs, mode {s.Mode}, port {s.Device.Port}");
    }

    private static void RenderMood(StaticSettings st, Span<float> rgb, int n, double t)
    {
        double speed = st.MoodSpeed;
        switch (st.MoodEffect)
        {
            case MoodEffect.ColorCycle:
            {
                var (r, g, b) = ColorProcessor.Hsv((float)(t * 6 * speed), 1, 1); // 60 s per cycle
                for (int i = 0; i < n; i++) { rgb[3 * i] = r; rgb[3 * i + 1] = g; rgb[3 * i + 2] = b; }
                break;
            }
            case MoodEffect.Rainbow:
            {
                for (int i = 0; i < n; i++)
                {
                    var (r, g, b) = ColorProcessor.Hsv((float)(360.0 * i / Math.Max(1, n) + t * 12 * speed), 1, 1);
                    rgb[3 * i] = r; rgb[3 * i + 1] = g; rgb[3 * i + 2] = b;
                }
                break;
            }
            case MoodEffect.Breathe:
            {
                int c = st.Color;
                float k = (float)(0.55 + 0.45 * Math.Sin(t * 2 * Math.PI / 6.0 * speed)); // 6 s per breath
                float r = ((c >> 16) & 0xFF) / 255f * k, g = ((c >> 8) & 0xFF) / 255f * k, b = (c & 0xFF) / 255f * k;
                for (int i = 0; i < n; i++) { rgb[3 * i] = r; rgb[3 * i + 1] = g; rgb[3 * i + 2] = b; }
                break;
            }
        }
    }

    private void PublishStats(double outFps, double capFps, double capMs, double procMs, PauseReason paused, ILedOverride? ov, bool force)
    {
        if (!force) return;
        var st = _device.Status;
        string text = st switch
        {
            DeviceStatus.Connected when ov is not null => $"Test pattern on {_device.PortName}",
            DeviceStatus.Connected when paused != PauseReason.None => paused.HasFlag(PauseReason.UserOff) ? "Lights off" : $"Paused ({Describe(paused)})",
            DeviceStatus.Connected => $"Connected on {_device.PortName}",
            DeviceStatus.PortBusy => $"{_device.PortName} is in use by another program (AmbiBox?)",
            DeviceStatus.Searching => "Looking for the LED controller… (plug in the USB cable)",
            _ => "Stopped",
        };
        Volatile.Write(ref _stats, new EngineStats(outFps, capFps, capMs, procMs, st, _device.PortName,
            _device.MaxFrameRate, _sampler.CurrentMonitor, paused, ov is not null, text));
    }

    private static string Describe(PauseReason p)
    {
        var parts = new List<string>();
        if (p.HasFlag(PauseReason.DisplayOff)) parts.Add("display off");
        if (p.HasFlag(PauseReason.Locked)) parts.Add("locked");
        if (p.HasFlag(PauseReason.Suspended)) parts.Add("sleep");
        return string.Join(", ", parts);
    }

    /// <summary>Stops the thread and turns the LEDs off.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _wake.Set();
        _thread?.Join(1500);
        _thread = null;
        try { _device.Blackout(); } catch (Exception ex) { Log.Warn($"Blackout failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        Stop();
        _device.Dispose();
        _sampler.Dispose();
        _wake.Dispose();
    }
}
