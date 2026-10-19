using System.Diagnostics;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.AlienFx;

/// <summary>Timing knobs of <see cref="AlienFxDevice"/> (tests shorten them).</summary>
internal sealed record AlienFxOptions
{
    /// <summary>
    /// LightFX goes through the Alienware Command Center service and the lighting controller's USB HID protocol;
    /// Prismatik drives it from its grab loop (typically 20–30 updates/s). 25 fps is a safe constant.
    /// </summary>
    public double FrameRate { get; init; } = 25;
    /// <summary>Retry interval while LightFX is missing or reports no devices.</summary>
    public int SearchIntervalMs { get; init; } = 5000;

    public static AlienFxOptions Default { get; } = new();
}

/// <summary>
/// Alienware AlienFX lighting through the Alienware LightFX 2.0 SDK (LightFX.dll), like Prismatik's LedDeviceAlienFx.
/// <para>
/// LED → light mapping: all lights of all LightFX devices form one list (device order, then light index). The N
/// RambiBox LEDs are split into that many contiguous groups and each light shows its group's average color. With one
/// LED (Prismatik's default for AlienFX) every light shows that LED's color — exactly Prismatik's behaviour; with as
/// many LEDs as lights each light gets its own LED. Brightness is always 255 (as Prismatik), ColorOrder is ignored
/// (LightFX takes logical RGB).
/// </para>
/// <para>
/// When LightFX.dll is absent the device stays <see cref="DeviceStatus.Searching"/> and logs once that Alienware LightFX is
/// not available. All LightFX calls happen on the device thread; <see cref="Blackout"/> resets the lights
/// (LFX_Reset + LFX_Update), <see cref="Dispose"/> also calls LFX_Release. Never throws.
/// </para>
/// </summary>
[LedDevice(DeviceType.AlienFx)]
public sealed class AlienFxDevice : ILedDevice
{
    internal const string NotAvailableMessage = "Alienware LightFX not available (Alienware Command Center/AlienFX not installed)";

    private readonly ILightFxProvider _provider;
    private readonly AlienFxOptions _opt;
    private readonly FramePacer _pacer;
    private readonly Func<bool> _isConnected;

    private readonly object _cfgLock = new();
    private Thread? _thread;

    private readonly AutoResetEvent _control = new(false);
    private volatile bool _stop, _suspended;
    private int _rescan, _disposed;
    private BlackoutRequest? _blackout;

    private volatile DeviceStatus _status = DeviceStatus.Stopped;
    private volatile string? _portName;

    // ---- device thread
    private HighResolutionTimer? _timer;
    private ILightFx? _api;
    private bool _initialized;
    private (uint Device, uint Light)[] _lights = [];
    private LfxColor[] _colors = [];

    public AlienFxDevice() : this(NativeLightFx.Shared, AlienFxOptions.Default) { }

    internal AlienFxDevice(ILightFxProvider provider, AlienFxOptions? options = null)
    {
        _provider = provider;
        _opt = options ?? AlienFxOptions.Default;
        _pacer = new FramePacer(_opt.FrameRate);
        _isConnected = () => _status == DeviceStatus.Connected;
    }

    // =====================================================================================
    // ILedDevice
    // =====================================================================================

    public DeviceStatus Status => _status;
    public string? PortName => _portName;
    public event Action? StatusChanged;
    public event Action<SerialPortInfo>? DeviceFound { add { } remove { } }
    public double MaxFrameRate => _pacer.FrameRate;
    public double FramesPerSecond => _status == DeviceStatus.Connected ? _pacer.FramesPerSecond : 0;

    public void Configure(DeviceSettings settings, int ledCount)
    {
        if (settings is null || Volatile.Read(ref _disposed) != 0) return;
        bool start;
        lock (_cfgLock)
        {
            start = _thread is null;
            if (start) _thread = new Thread(ThreadMain) { IsBackground = true, Name = "RambiBox AlienFX" };
        }
        if (start)
        {
            Log.Info($"AlienFX: starting ({ledCount} LEDs)");
            _thread!.Start();
        }
        if (_suspended)
        {
            _suspended = false;
            Log.Info("AlienFX: resuming after blackout");
        }
        Signal();
    }

    public void Submit(ReadOnlySpan<byte> rgb)
    {
        if (_status != DeviceStatus.Connected || Volatile.Read(ref _disposed) != 0) return;
        _pacer.Post(rgb);
    }

    public bool WaitForSlot(int timeoutMs) => _pacer.WaitForSlot(timeoutMs, _isConnected);

    public void Rescan()
    {
        Interlocked.Exchange(ref _rescan, 1);
        Signal();
    }

    public void Blackout()
    {
        var t = _thread;
        if (t is null || !t.IsAlive || Volatile.Read(ref _disposed) != 0) return;
        if (Thread.CurrentThread == t) { _suspended = true; return; }
        var req = new BlackoutRequest();
        Volatile.Write(ref _blackout, req);
        Signal();
        if (!req.Done.Wait(400)) Log.Warn("AlienFX: blackout did not complete in time");
    }

    public IReadOnlyList<SerialPortInfo> ListPorts() => Array.Empty<SerialPortInfo>();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop = true;
        Signal();
        Thread? t;
        lock (_cfgLock) t = _thread;
        bool joined = true;
        if (t is not null && t.IsAlive && Thread.CurrentThread != t)
        {
            joined = t.Join(2000);
            if (!joined) Log.Warn("AlienFX: device thread did not stop in time");
        }
        if (joined)
        {
            _control.Dispose();
            _pacer.Dispose();
        }
    }

    internal long FramesWritten => _pacer.FramesWritten;
    internal int LightCount => Volatile.Read(ref _lights).Length;

    // =====================================================================================
    // Mapping
    // =====================================================================================

    /// <summary>
    /// Maps N LEDs (R,G,B triplets) onto <paramref name="lights"/>.Length lights: light j shows the rounded average of
    /// LEDs [j·N/L, (j+1)·N/L) — at least one LED, so with fewer LEDs than lights an LED drives several lights.
    /// </summary>
    internal static void MapToLights(ReadOnlySpan<byte> rgb, Span<LfxColor> lights)
    {
        int n = rgb.Length / 3, count = lights.Length;
        if (n == 0)
        {
            lights.Fill(LfxColor.Black);
            return;
        }
        for (int j = 0; j < count; j++)
        {
            int start = (int)((long)j * n / count);
            int end = (int)((long)(j + 1) * n / count);
            if (end <= start) end = start + 1;
            int r = 0, g = 0, b = 0, cnt = end - start;
            for (int i = start; i < end; i++)
            {
                r += rgb[i * 3];
                g += rgb[i * 3 + 1];
                b += rgb[i * 3 + 2];
            }
            lights[j] = new LfxColor((byte)((r + cnt / 2) / cnt), (byte)((g + cnt / 2) / cnt), (byte)((b + cnt / 2) / cnt), 0xFF);
        }
    }

    // =====================================================================================
    // Device thread
    // =====================================================================================

    private void ThreadMain()
    {
        _timer = new HighResolutionTimer();
        try
        {
            while (!_stop)
            {
                try
                {
                    if (TakeBlackout() is { } req)
                    {
                        if (_initialized) ResetLights();
                        _suspended = true;
                        SetStatus(DeviceStatus.Stopped, null);
                        req.Done.Set();
                        continue;
                    }
                    if (_suspended)
                    {
                        SetStatus(DeviceStatus.Stopped, null);
                        _control.WaitOne(1000);
                        continue;
                    }
                    if (Connect()) RunConnected();
                }
                catch (Exception ex)
                {
                    Log.Error("AlienFX: device thread error", ex);
                    ReleaseApi();
                    try { _control.WaitOne(1000); } catch { Thread.Sleep(1000); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("AlienFX: device thread crashed", ex);
        }
        finally
        {
            ReleaseApi();
            TakeBlackout()?.Done.Set();
            SetStatus(DeviceStatus.Stopped, null);
            _timer.Dispose();
        }
    }

    private bool Interrupted() => _stop || _suspended || Volatile.Read(ref _blackout) is not null;

    private bool Connect()
    {
        SetStatus(DeviceStatus.Searching, null);
        string? lastProblem = null;
        while (!Interrupted())
        {
            Interlocked.Exchange(ref _rescan, 0);
            string problem;
            if (!_initialized)
            {
                string? reason = null;
                _api ??= SafeGetApi(out reason);
                if (_api is null)
                {
                    problem = $"{NotAvailableMessage}: {reason}";
                    goto wait;
                }
                uint r = _api.Initialize();
                if (r is not (LfxResult.Success or LfxResult.NoDevs))
                {
                    problem = $"LightFX initialization failed ({LfxResult.Name(r)})";
                    goto wait;
                }
                _initialized = true;
            }

            var lights = EnumerateLights(out string summary);
            if (lights.Length > 0)
            {
                _lights = lights;
                _colors = new LfxColor[lights.Length];
                _api!.Reset();
                Log.Info($"AlienFX: connected, {summary}");
                return true;
            }
            problem = "LightFX reports no Alienware lighting devices";
            ReleaseApi(); // re-initialize next time so newly attached devices are seen (LFX2.h PnP note)

        wait:
            if (problem != lastProblem) Log.Info($"AlienFX: {problem}");
            lastProblem = problem;
            SetStatus(DeviceStatus.Searching, null);
            _control.WaitOne(_opt.SearchIntervalMs);
        }
        return false;
    }

    private void RunConnected()
    {
        _pacer.SetFrameRate(_opt.FrameRate);
        _pacer.Reset();
        SetStatus(DeviceStatus.Connected, $"AlienFX ({_lights.Length} lights)");
        bool black = false;
        BlackoutRequest? req = null;
        string? lost = null;
        try
        {
            while (true)
            {
                if (_stop || _suspended) { black = true; break; }
                if ((req = TakeBlackout()) is not null) { black = true; _suspended = true; break; }
                if (Interlocked.Exchange(ref _rescan, 0) != 0 && LightsChanged()) { lost = "AlienFX devices changed"; break; }

                if (!TrySendPending(out lost))
                {
                    if (lost is not null) break;
                    _pacer.WaitForFrame(1000);
                }
            }
        }
        catch (Exception ex)
        {
            lost = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (black) ResetLights();
        if (lost is not null)
        {
            Log.Warn($"AlienFX: {lost}; searching again");
            ReleaseApi();
        }
        if (req is not null)
        {
            Log.Info("AlienFX: lights reset (blackout)");
            SetStatus(DeviceStatus.Stopped, null);
            req.Done.Set();
        }
        else if (_stop || _suspended)
        {
            SetStatus(DeviceStatus.Stopped, null);
        }
        else
        {
            SetStatus(DeviceStatus.Searching, null);
        }
    }

    private bool TrySendPending(out string? lost)
    {
        lost = null;
        if (!_pacer.TryTake(_timer!, out var frame)) return false;
        long t0 = Stopwatch.GetTimestamp();
        MapToLights(frame, _colors);
        var api = _api!;
        for (int i = 0; i < _lights.Length; i++)
        {
            uint r = api.SetLightColor(_lights[i].Device, _lights[i].Light, in _colors[i]);
            if (r is LfxResult.NoInit or LfxResult.NoDevs)
            {
                lost = $"LFX_SetLightColor returned {LfxResult.Name(r)}";
                return false;
            }
        }
        uint u = api.Update();
        if (u != LfxResult.Success)
        {
            lost = $"LFX_Update returned {LfxResult.Name(u)}";
            return false;
        }
        _pacer.MarkWritten(t0);
        return true;
    }

    private (uint, uint)[] EnumerateLights(out string summary)
    {
        var api = _api!;
        var lights = new List<(uint, uint)>();
        var parts = new List<string>();
        if (api.GetNumDevices(out uint devices) == LfxResult.Success)
        {
            for (uint d = 0; d < Math.Min(devices, 64u); d++)
            {
                if (api.GetNumLights(d, out uint n) != LfxResult.Success || n == 0) continue;
                n = Math.Min(n, 1024u);
                for (uint l = 0; l < n; l++) lights.Add((d, l));
                string desc = api.GetDeviceDescription(d, out string s, out _) == LfxResult.Success && s.Length > 0 ? s : $"device {d}";
                parts.Add($"{desc}: {n} lights");
            }
        }
        summary = parts.Count == 0 ? "no lights" : string.Join(", ", parts);
        return lights.ToArray();
    }

    private bool LightsChanged()
    {
        var now = EnumerateLights(out _);
        return !now.AsSpan().SequenceEqual(_lights);
    }

    private void ResetLights()
    {
        try
        {
            _pacer.DropPending();
            _api?.Reset();
            _api?.Update();
        }
        catch (Exception ex)
        {
            Log.Warn($"AlienFX: resetting the lights failed: {ex.Message}");
        }
    }

    private void ReleaseApi()
    {
        if (!_initialized) return;
        _initialized = false;
        try { _api?.Release(); }
        catch (Exception ex) { Log.Warn($"AlienFX: LFX_Release failed: {ex.Message}"); }
    }

    private ILightFx? SafeGetApi(out string? reason)
    {
        try { return _provider.TryGet(out reason); }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    private void Signal()
    {
        try { _control.Set(); } catch (ObjectDisposedException) { /* disposed */ }
        _pacer.Signal();
    }

    private BlackoutRequest? TakeBlackout() => Interlocked.Exchange(ref _blackout, null);

    private void SetStatus(DeviceStatus status, string? port)
    {
        if (_status == status && string.Equals(_portName, port, StringComparison.Ordinal)) return;
        _portName = port;
        _status = status;
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { Log.Error("AlienFX: StatusChanged handler failed", ex); }
    }

    private sealed class BlackoutRequest
    {
        public readonly ManualResetEventSlim Done = new(false);
    }
}
