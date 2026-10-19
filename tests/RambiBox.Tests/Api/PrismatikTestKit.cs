using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RambiBox.Core.Api;
using RambiBox.Core.Config;

namespace RambiBox.Tests.Api;

/// <summary>In-memory application side for the API server.</summary>
internal sealed class FakePrismatikHost : IPrismatikHost
{
    private readonly object _lock = new();

    public PrismatikStatus Status = PrismatikStatus.On;
    public bool Enabled = true;
    public LightMode Mode = LightMode.Ambilight;
    public double Brightness = 1.0;
    public int SmoothingMs = 100;
    public double Gamma = 1.0;
    public double Fps = 33.62;
    public PixelRect Screen = new(0, 0, 2560, 1440);
    public List<LedZone> Zones;
    public byte[] Colors;
    public List<PixelRect> Monitors = [new(0, 0, 2560, 1440), new(2560, 0, 1920, 1080)];
    public readonly List<PrismatikChange> Changes = new();
    public readonly List<PrismatikControl> Controls = new();

    public FakePrismatikHost(int leds = 10)
    {
        // a row of zones along the top edge: zone i covers x = i/leds … (i+1)/leds, y = 0 … 0.1
        Zones = Enumerable.Range(0, leds).Select(i => new LedZone(i / (float)leds, 0, (i + 1) / (float)leds, 0.1f)).ToList();
        Colors = Enumerable.Range(0, leds * 3).Select(i => (byte)(i * 7 % 256)).ToArray();
    }

    public PrismatikControl LastControl { get { lock (_lock) return Controls.Count == 0 ? PrismatikControl.None : Controls[^1]; } }

    /// <summary>Colors the API override currently shows (null when the API has no control).</summary>
    public byte[]? ShownColors => (LastControl.Override as ApiColorOverride)?.GetColors();

    public PrismatikHostState GetState()
    {
        lock (_lock)
            return new PrismatikHostState(Enabled ? Status : PrismatikStatus.Off, Mode, Brightness, SmoothingMs, Gamma, Fps, Screen, Zones.ToArray());
    }

    public byte[] GetColors() { lock (_lock) return (byte[])Colors.Clone(); }

    public IReadOnlyList<PixelRect> GetMonitors() { lock (_lock) return Monitors.ToArray(); }

    public Task<bool> ApplyAsync(PrismatikChange change)
    {
        lock (_lock)
        {
            Changes.Add(change);
            if (change.Enabled is { } e) Enabled = e;
            if (change.Mode is { } m) Mode = m;
            if (change.Brightness is { } b) Brightness = b;
            if (change.SmoothingMs is { } s) SmoothingMs = s;
            if (change.Gamma is { } g) Gamma = g;
            if (change.Zones is { } zones)
                foreach (var z in zones) Zones[z.Index] = new LedZone(z.Left, z.Top, z.Right, z.Bottom);
        }
        return Task.FromResult(true);
    }

    public void OnApiControlChanged(PrismatikControl control) { lock (_lock) Controls.Add(control); }
}

/// <summary>A raw line client (what Prismatik clients do: write "cmd\n", read until "\r\n").</summary>
internal sealed class ApiClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly List<byte> _pending = new();

    public string Greeting { get; private set; } = "";

    private ApiClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<ApiClient> ConnectAsync(int port, bool readGreeting = true)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var c = new ApiClient(tcp);
        if (readGreeting) c.Greeting = await c.ReadLineAsync() + "\r\n";
        return c;
    }

    public async Task WriteAsync(string raw) => await _stream.WriteAsync(Encoding.UTF8.GetBytes(raw));

    public async Task<string> SendAsync(string command)
    {
        await WriteAsync(command + "\n");
        return await ReadLineAsync();
    }

    /// <summary>Reads one "\r\n"-terminated line (without the terminator).</summary>
    public async Task<string> ReadLineAsync(int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            for (int i = 1; i < _pending.Count; i++)
                if (_pending[i - 1] == '\r' && _pending[i] == '\n')
                {
                    var line = Encoding.UTF8.GetString(_pending.GetRange(0, i - 1).ToArray());
                    _pending.RemoveRange(0, i + 1);
                    return line;
                }
            if (!await FillAsync(timeoutMs - (int)sw.ElapsedMilliseconds))
                throw new IOException($"connection closed / timed out; pending: {Encoding.UTF8.GetString(_pending.ToArray())}");
        }
    }

    /// <summary>Reads exactly the byte length of <paramref name="expected"/> and returns it as text.</summary>
    public async Task<string> ReadExactlyAsync(string expected, int timeoutMs = 5000)
    {
        int need = Encoding.UTF8.GetByteCount(expected);
        var sw = Stopwatch.StartNew();
        while (_pending.Count < need)
            if (!await FillAsync(timeoutMs - (int)sw.ElapsedMilliseconds)) break;
        int take = Math.Min(need, _pending.Count);
        var text = Encoding.UTF8.GetString(_pending.GetRange(0, take).ToArray());
        _pending.RemoveRange(0, take);
        return text;
    }

    /// <summary>True when the server closed the connection within the timeout (no more data).</summary>
    public async Task<bool> WaitClosedAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buf = new byte[8192];
        while (true)
        {
            try
            {
                int n = await _stream.ReadAsync(buf, cts.Token);
                if (n == 0) return true;
                _pending.AddRange(buf.AsSpan(0, n).ToArray());
            }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { return true; }
            catch (SocketException) { return true; }
        }
    }

    private async Task<bool> FillAsync(int timeoutMs)
    {
        if (timeoutMs <= 0) return false;
        var buf = new byte[8192];
        using var cts = new CancellationTokenSource(timeoutMs);
        int n;
        try { n = await _stream.ReadAsync(buf, cts.Token); }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        if (n == 0) return false;
        _pending.AddRange(buf.AsSpan(0, n).ToArray());
        return true;
    }

    public void Dispose()
    {
        try { _tcp.Client.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        _tcp.Dispose();
    }
}

internal static class PrismatikTestKit
{
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static PrismatikApiServer Start(FakePrismatikHost host, string apiKey = "", Func<PrismatikApiServer>? create = null)
    {
        var server = create?.Invoke() ?? new PrismatikApiServer(host, "9.9.9") { AuthFailureDelay = TimeSpan.Zero };
        for (int attempt = 0; ; attempt++)
        {
            server.Apply(new ApiSettings { Enabled = true, Port = FreePort(), ApiKey = apiKey });
            if (server.Status.Listening || attempt == 5) break;
            server.Stop();
        }
        Assert.True(server.Status.Listening, server.Status.Error);
        return server;
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("condition not met in time");
            await Task.Delay(20);
        }
    }
}
