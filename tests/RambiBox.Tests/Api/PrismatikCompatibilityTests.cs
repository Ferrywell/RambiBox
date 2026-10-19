using System.Globalization;
using System.Text.RegularExpressions;
using RambiBox.Core.Config;
using static RambiBox.Tests.Api.PrismatikTestKit;

namespace RambiBox.Tests.Api;

/// <summary>
/// Replays what real Prismatik clients send, with their own parsing and expectations:
/// the Python "lightpack" library (tremby/py-lightpack 2.2.0, lightpack.py) and a Home Assistant style session.
/// </summary>
public class PrismatikCompatibilityTests
{
    /// <summary>The bits of py-lightpack's Lightpack class that talk to the server, ported 1:1.</summary>
    private sealed class PyLightpack(ApiClient c)
    {
        public double ApiVersion;
        private int? _countLeds;

        public void CheckGreeting(string greeting)
        {
            // match = re.findall(r'API v?(\d+(?:\.\d+)?)', greeting); match.sort(reverse=True)
            var versions = Regex.Matches(greeting, @"API v?(\d+(?:\.\d+)?)").Select(m => m.Groups[1].Value)
                .OrderByDescending(v => Version.Parse(v)).ToList();
            Assert.NotEmpty(versions);
            ApiVersion = double.Parse(versions[0], CultureInfo.InvariantCulture);
            Assert.InRange(ApiVersion, 1.4, 2.2); // API_VERSION_GTE / API_VERSION_LTE
        }

        private static string? Payload(string s) => s.Split(':', 2) is { Length: 2 } p ? p[1] : null;
        private Task<string> SendAndReceive(string cmd) => c.SendAsync(cmd);
        private async Task<string?> SendAndReceivePayload(string cmd) => Payload(await SendAndReceive(cmd));

        private async Task SendAndExpect(string cmd, string expected) => Assert.Equal(expected, await SendAndReceive(cmd));
        private Task SendAndExpectOk(string cmd) => SendAndExpect(cmd, "ok");
        private Task SendAndExpectSuccess(string cmd) => SendAndExpect(cmd, $"{cmd.Split(':')[0]}:success");

        public async Task ApiKey(string key) => Assert.Equal("ok", await SendAndReceive($"apikey:{key}"));
        public Task Lock() => SendAndExpectSuccess("lock");
        public Task Unlock() => SendAndExpectSuccess("unlock");
        public async Task<int> GetCountLeds(bool fresh = true)
        {
            if (fresh || _countLeds is null) _countLeds = int.Parse((await SendAndReceivePayload("getcountleds"))!);
            return _countLeds.Value;
        }
        private async Task<int> LedIndex(int led)
        {
            int index = led + 1;
            Assert.True(index <= await GetCountLeds(fresh: false));
            return index;
        }
        private async Task<string> LedColourDef(int led, (int R, int G, int B) rgb) => $"{await LedIndex(led)}-{rgb.R},{rgb.G},{rgb.B}";
        public async Task SetColour(int led, (int, int, int) rgb) => await SendAndExpectOk($"setcolor:{await LedColourDef(led, rgb)}");
        public async Task SetColours(params (int Led, (int, int, int) Rgb)[] args)
        {
            var defs = new List<string>();
            foreach (var a in args) defs.Add(await LedColourDef(a.Led, a.Rgb));
            await SendAndExpectOk("setcolor:" + string.Join(";", defs));
        }
        public async Task SetColourToAll((int, int, int) rgb)
        {
            var defs = new List<string>();
            for (int led = 0; led < await GetCountLeds(fresh: false); led++) defs.Add(await LedColourDef(led, rgb));
            await SendAndExpectOk("setcolor:" + string.Join(";", defs));
        }
        public async Task<Dictionary<int, int[]>> GetColoursFromAll()
        {
            var colours = new Dictionary<int, int[]>();
            foreach (var cmd in (await SendAndReceivePayload("getcolors"))!.TrimEnd(';').Split(';'))
            {
                var parts = cmd.Split('-', 2);
                colours[int.Parse(parts[0])] = parts[1].Split(',', 3).Select(int.Parse).ToArray();
            }
            return colours;
        }
        public async Task<Dictionary<int, int[]>> GetLedSizes()
        {
            var sizes = new Dictionary<int, int[]>();
            foreach (var cmd in (await SendAndReceivePayload("getleds"))!.TrimEnd(';').Split(';'))
            {
                var parts = cmd.Split('-', 2);
                sizes[int.Parse(parts[0])] = parts[1].Split(',', 4).Select(int.Parse).ToArray();
            }
            return sizes;
        }
        public async Task<int[]> GetScreenSize() => (await SendAndReceivePayload("getscreensize"))!.Split(',', 4).Select(int.Parse).ToArray();
        public async Task<double> GetGamma() => double.Parse((await SendAndReceivePayload("getgamma"))!, CultureInfo.InvariantCulture);
        public async Task<int> GetSmoothness() => int.Parse((await SendAndReceivePayload("getsmooth"))!);
        public async Task<int> GetBrightness() => int.Parse((await SendAndReceivePayload("getbrightness"))!);
        public async Task<string> GetMode() => (await SendAndReceivePayload("getmode"))!;
        public async Task<string> GetPersistence() => (await SendAndReceivePayload("getpersistonunlock"))!;
        public async Task<List<string>> GetProfiles() => (await SendAndReceivePayload("getprofiles"))!.TrimEnd(';').Split(';').ToList();
        public async Task<string> GetProfile() => (await SendAndReceivePayload("getprofile"))!;
        public async Task<string> GetStatus() => (await SendAndReceivePayload("getstatus"))!;
        public async Task<string> GetLockStatus() => (await SendAndReceivePayload("getlockstatus"))!;
        public async Task<string> GetApiStatus() => (await SendAndReceivePayload("getstatusapi"))!;
        public Task SetGamma(double g) => SendAndExpectOk($"setgamma:{g.ToString("0.0###", CultureInfo.InvariantCulture)}"); // Python '%s' % 2.0 == "2.0"
        public Task SetSmoothness(int s) => SendAndExpectOk($"setsmooth:{s}");
        public Task SetBrightness(int b) => SendAndExpectOk($"setbrightness:{b}");
        public Task SetMode(string m) => SendAndExpectOk($"setmode:{m}");
        public Task SetProfile(string p) => SendAndExpectOk($"setprofile:{p}");
        public Task Persist() => SendAndExpectOk("setpersistonunlock:on");
        public Task Unpersist() => SendAndExpectOk("setpersistonunlock:off");
        public Task TurnOn() => SendAndExpectOk("setstatus:on");
        public Task TurnOff() => SendAndExpectOk("setstatus:off");
        public async Task Disconnect()
        {
            // unlock() but ignore CommandFailedError, then close
            await SendAndReceive("unlock");
            c.Dispose();
        }
    }

    [Fact]
    public async Task PythonLightpackLibrary_TypicalSession()
    {
        var host = new FakePrismatikHost(10);
        using var server = Start(host, apiKey: "{1ccf5dca-119d-45a0-a683-7d90a00c418f}");
        var c = await ApiClient.ConnectAsync(server.Port);
        var lp = new PyLightpack(c);

        // lp.connect()
        lp.CheckGreeting(c.Greeting);
        Assert.Equal(2.2, lp.ApiVersion);
        await lp.ApiKey("{1ccf5dca-119d-45a0-a683-7d90a00c418f}");

        // read-only calls
        Assert.Equal("on", await lp.GetStatus());
        Assert.Equal(new[] { "Default" }, await lp.GetProfiles());
        Assert.Equal("Default", await lp.GetProfile());
        Assert.Equal(10, await lp.GetCountLeds());
        Assert.Equal(new[] { 0, 0, 2560, 1440 }, await lp.GetScreenSize());
        var sizes = await lp.GetLedSizes();
        Assert.Equal(10, sizes.Count);
        Assert.Equal(new[] { 256, 0, 256, 144 }, sizes[1]);
        Assert.Equal(10, (await lp.GetColoursFromAll()).Count);
        Assert.Equal(1.0, await lp.GetGamma());
        Assert.Equal(25, await lp.GetSmoothness());
        Assert.Equal(100, await lp.GetBrightness());
        Assert.Equal("ambilight", await lp.GetMode());
        Assert.Equal("off", await lp.GetPersistence());
        Assert.Equal("idle", await lp.GetApiStatus());
        Assert.Equal("no", await lp.GetLockStatus());

        // lp.lock(); colors; settings
        await lp.Lock();
        Assert.Equal("busy", await lp.GetApiStatus());
        Assert.Equal("ok", await lp.GetLockStatus());
        await lp.SetColourToAll((255, 128, 0));
        Assert.Equal(Enumerable.Repeat(new byte[] { 255, 128, 0 }, 10).SelectMany(x => x).ToArray(), host.ShownColors);
        await lp.SetColour(0, (0, 0, 255));
        await lp.SetColours((1, (1, 2, 3)), (9, (4, 5, 6)));
        var shown = host.ShownColors!;
        Assert.Equal(new byte[] { 0, 0, 255, 1, 2, 3, 255, 128, 0 }, shown[..9]);
        Assert.Equal(new byte[] { 4, 5, 6 }, shown[27..30]);

        await lp.SetBrightness(40);
        await lp.SetSmoothness(10);
        await lp.SetGamma(2.0);
        Assert.Equal(0.4, host.Brightness, 6);
        Assert.Equal(40, host.SmoothingMs);
        Assert.Equal(2.0, host.Gamma, 6);
        Assert.Equal(40, await lp.GetBrightness());
        Assert.Equal(10, await lp.GetSmoothness());
        Assert.Equal(2.0, await lp.GetGamma());

        await lp.SetMode("moodlamp");
        Assert.Equal("moodlamp", await lp.GetMode());
        await lp.SetMode("ambilight");
        await lp.SetProfile("Default");
        await lp.TurnOff();
        Assert.Equal("off", await lp.GetStatus());
        await lp.TurnOn();
        Assert.Equal("on", await lp.GetStatus());
        await lp.Persist();
        Assert.Equal("on", await lp.GetPersistence());
        await lp.Unpersist();

        await lp.Unlock();
        Assert.False(host.LastControl.Active);
        await lp.Lock();
        await lp.Disconnect(); // unlock + close
        await WaitUntil(() => !host.LastControl.Active && server.Status.Clients == 0);
    }

    [Fact]
    public async Task HomeAssistantStyle_PollingAndLightService()
    {
        var host = new FakePrismatikHost(10);
        using var server = Start(host);

        // A long-lived polling connection (state updates) ...
        using var poll = await ApiClient.ConnectAsync(server.Port);
        Assert.StartsWith("Lightpack API v1.4 - Prismatik API v2.2", poll.Greeting);
        Assert.Equal("status:on", await poll.SendAsync("getstatus"));
        Assert.Equal("profile:Default", await poll.SendAsync("getprofile"));
        Assert.Equal("profiles:Default;", await poll.SendAsync("getprofiles"));
        Assert.Equal("brightness:100", await poll.SendAsync("getbrightness"));
        Assert.StartsWith("colors:0-", await poll.SendAsync("getcolors"));
        Assert.Equal("countleds:10", await poll.SendAsync("getcountleds"));

        // ... and light.turn_on(rgb_color=[0, 255, 64], brightness=50%) from a second connection.
        using var cmd = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await cmd.SendAsync("lock"));
        Assert.Equal("ok", await cmd.SendAsync("setpersistonunlock:on"));
        Assert.Equal("ok", await cmd.SendAsync("setstatus:on"));
        Assert.Equal("ok", await cmd.SendAsync("setcolor:" + string.Concat(Enumerable.Range(1, 10).Select(i => $"{i}-0,255,64;"))));
        Assert.Equal("ok", await cmd.SendAsync("setbrightness:50"));
        Assert.Equal("statusapi:busy", await poll.SendAsync("getstatusapi"));
        Assert.Equal("lock:busy", await poll.SendAsync("lock"));
        Assert.Equal("unlock:success", await cmd.SendAsync("unlock"));

        // the color stays (persist on unlock) and the polling connection sees the new state
        Assert.True(host.LastControl.Persisting);
        Assert.Equal(Enumerable.Repeat(new byte[] { 0, 255, 64 }, 10).SelectMany(x => x).ToArray(), host.ShownColors);
        Assert.Equal("brightness:50", await poll.SendAsync("getbrightness"));
        Assert.Equal("statusapi:idle", await poll.SendAsync("getstatusapi"));

        // light.turn_off
        Assert.Equal("lock:success", await cmd.SendAsync("lock"));
        Assert.Equal("ok", await cmd.SendAsync("setstatus:off"));
        Assert.Equal("unlock:success", await cmd.SendAsync("unlock"));
        Assert.Equal("status:off", await poll.SendAsync("getstatus"));
        Assert.False(host.Enabled);
        Assert.Contains(host.Changes, ch => ch.Enabled == false);
        Assert.Equal(LightMode.Ambilight, host.Mode);
    }
}
