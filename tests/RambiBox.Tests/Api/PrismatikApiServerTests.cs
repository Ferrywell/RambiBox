using System.Net;
using System.Text;
using RambiBox.Core.Api;
using RambiBox.Core.Config;
using static RambiBox.Tests.Api.PrismatikTestKit;

namespace RambiBox.Tests.Api;

public class PrismatikApiServerTests
{
    // ------------------------------------------------------------------ greeting / get commands

    [Fact]
    public async Task Greeting_IsPrismatiks()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("Lightpack API v1.4 - Prismatik API v2.2 (type \"help\" for more info)\r\n", c.Greeting);
    }

    [Fact]
    public async Task GetCommands_AnswerInPrismatikFormat()
    {
        var host = new FakePrismatikHost(10);
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);

        Assert.Equal("status:on", await c.SendAsync("getstatus"));
        Assert.Equal("statusapi:idle", await c.SendAsync("getstatusapi"));
        Assert.Equal("profiles:Default;", await c.SendAsync("getprofiles"));
        Assert.Equal("profile:Default", await c.SendAsync("getprofile"));
        Assert.Equal("countleds:10", await c.SendAsync("getcountleds"));
        Assert.Equal("leds:" + string.Concat(Enumerable.Range(0, 10).Select(i => $"{i}-{i * 256},0,256,144;")), await c.SendAsync("getleds"));
        Assert.Equal("colors:" + string.Concat(Enumerable.Range(0, 10).Select(i => $"{i}-{i * 21 % 256},{(i * 21 + 7) % 256},{(i * 21 + 14) % 256};")),
            await c.SendAsync("getcolors"));
        Assert.Equal("fps:33.62", await c.SendAsync("getfps"));
        Assert.Equal("screensize:0,0,2560,1440", await c.SendAsync("getscreensize"));
        Assert.Equal("brightness:100", await c.SendAsync("getbrightness"));
        Assert.Equal("smooth:25", await c.SendAsync("getsmooth")); // 100 ms / 4 ms per step
        Assert.Equal("gamma:1", await c.SendAsync("getgamma"));
        Assert.Equal("mode:ambilight", await c.SendAsync("getmode"));
        Assert.Equal("persistonunlock:off", await c.SendAsync("getpersistonunlock"));
        Assert.Equal("lockstatus:no", await c.SendAsync("getlockstatus"));
        Assert.Equal("device:Adalight", await c.SendAsync("getdevice"));
        Assert.Equal("devices:Adalight;", await c.SendAsync("getdevices"));
        Assert.Equal("maxleds:1000", await c.SendAsync("getmaxleds"));
        Assert.Equal("countmonitors:2", await c.SendAsync("getcountmonitors"));
        Assert.Equal("sizemonitor:2560,0,1920,1080", await c.SendAsync("getsizemonitor:1"));
        Assert.Equal("error", await c.SendAsync("getsizemonitor:2"));
        Assert.Equal("error", await c.SendAsync("getsizemonitor:x"));
        Assert.Equal("error", await c.SendAsync("guid:{1ccf5dca-119d-45a0-a683-7d90a00c418f}"));
        Assert.Equal("deprecated command, not supported", await c.SendAsync("setdevice:Adalight"));
        Assert.Equal("deprecated command, not supported", await c.SendAsync("setcountleds:20"));
        Assert.Equal("unknown command", await c.SendAsync("getsomething"));
        Assert.Equal("unknown command", await c.SendAsync("GETSTATUS")); // case-sensitive like Prismatik
        Assert.Equal("unknown command", await c.SendAsync("setcolor")); // set-commands need the colon
    }

    [Theory]
    [InlineData(PrismatikStatus.On, "status:on")]
    [InlineData(PrismatikStatus.DeviceError, "status:device error")]
    [InlineData(PrismatikStatus.Unknown, "status:unknown")]
    public async Task GetStatus_MapsDeviceState(PrismatikStatus status, string expected)
    {
        var host = new FakePrismatikHost { Status = status };
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal(expected, await c.SendAsync("getstatus"));
        host.Enabled = false;
        Assert.Equal("status:off", await c.SendAsync("getstatus"));
    }

    [Theory]
    [InlineData(LightMode.Ambilight, "mode:ambilight")]
    [InlineData(LightMode.StaticColor, "mode:moodlamp")]
    [InlineData(LightMode.Mood, "mode:moodlamp")]
    public async Task GetMode_ReportsStaticAndMoodAsMoodlamp(LightMode mode, string expected)
    {
        var host = new FakePrismatikHost { Mode = mode };
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal(expected, await c.SendAsync("getmode"));
    }

    [Fact]
    public async Task Help_ShortHelp_And_Exit()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host, apiKey: "secret"); // help/?/exit work without the key
        using var c = await ApiClient.ConnectAsync(server.Port);

        await c.WriteAsync("help\n");
        var help = PrismatikProtocol.HelpMessage("9.9.9");
        Assert.Equal(help, await c.ReadExactlyAsync(help));
        Assert.Contains("setcolor:1-255,255,30;2-12,12,12;3-1,2,3;", help);
        Assert.StartsWith("\r\nRambiBox 9.9.9, Prismatik API 2.2\r\n", help);

        await c.WriteAsync("?\n");
        var shortHelp = PrismatikProtocol.ShortHelpMessage();
        Assert.Equal(shortHelp, await c.ReadExactlyAsync(shortHelp));
        Assert.Contains("\tapikey, lock, unlock, getstatus,", shortHelp);
        Assert.All(shortHelp.Split("\r\n"), l => Assert.True(l.Length < 90, l));

        Assert.Equal("Goodbye!", await c.SendAsync("exit"));
        Assert.True(await c.WaitClosedAsync());
    }

    [Fact]
    public async Task CrLf_EmptyLines_And_PipelinedCommands()
    {
        var host = new FakePrismatikHost(10);
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        await c.WriteAsync("getstatus\r\n\n  \r\ngetcountleds\n  getprofile  \r\n");
        Assert.Equal("status:on", await c.ReadLineAsync());
        Assert.Equal("countleds:10", await c.ReadLineAsync()); // empty lines get no answer
        Assert.Equal("profile:Default", await c.ReadLineAsync()); // trimmed like Prismatik
    }

    // ------------------------------------------------------------------ apikey

    [Fact]
    public async Task ApiKey_RequiredUntilItMatches()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host, apiKey: "IDDQD");
        using var c = await ApiClient.ConnectAsync(server.Port);

        Assert.Equal("authorization required", await c.SendAsync("getstatus"));
        Assert.Equal("authorization required", await c.SendAsync("lock"));
        Assert.Equal("authorization required", await c.SendAsync("unknowncommand"));
        Assert.Equal("fail", await c.SendAsync("apikey:iddqd"));
        Assert.Equal("fail", await c.SendAsync("apikey:"));
        Assert.Equal("authorization required", await c.SendAsync("getstatus"));
        Assert.Equal("ok", await c.SendAsync("apikey:IDDQD"));
        Assert.Equal("status:on", await c.SendAsync("getstatus"));
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        // a wrong key afterwards revokes the authorization (Prismatik behavior)
        Assert.Equal("fail", await c.SendAsync("apikey:nope"));
        Assert.Equal("authorization required", await c.SendAsync("getstatus"));

        // another client is not authorized by the first one
        using var d = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("authorization required", await d.SendAsync("getcountleds"));
    }

    [Fact]
    public async Task ApiKey_WithoutConfiguredKey_AlwaysOk()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("ok", await c.SendAsync("apikey:whatever"));
        Assert.Equal("status:on", await c.SendAsync("getstatus"));
    }

    [Fact]
    public async Task ChangingTheKey_RestartsAndDropsClients()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host);
        int port = server.Port;
        using var c = await ApiClient.ConnectAsync(port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));

        server.Apply(new ApiSettings { Enabled = true, Port = port, ApiKey = "new" });
        Assert.True(await c.WaitClosedAsync());
        Assert.False(host.LastControl.Active);

        using var d = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("authorization required", await d.SendAsync("getstatus"));
    }

    // ------------------------------------------------------------------ lock

    [Fact]
    public async Task Lock_IsExclusive_BusyForOthers()
    {
        var host = new FakePrismatikHost(10);
        using var server = Start(host);
        using var a = await ApiClient.ConnectAsync(server.Port);
        using var b = await ApiClient.ConnectAsync(server.Port);

        Assert.Equal("not locked", await a.SendAsync("setcolor:1-1,2,3"));
        Assert.Equal("not locked", await a.SendAsync("setbrightness:50"));
        Assert.Equal("unlock:not locked", await a.SendAsync("unlock"));

        Assert.Equal("lock:success", await a.SendAsync("lock"));
        Assert.True(host.LastControl.Locked);
        Assert.Equal("127.0.0.1", host.LastControl.Owner);
        Assert.Equal("lock:success", await a.SendAsync("lock")); // re-lock by the owner is fine
        Assert.Equal("lockstatus:ok", await a.SendAsync("getlockstatus"));

        Assert.Equal("lock:busy", await b.SendAsync("lock"));
        Assert.Equal("statusapi:busy", await b.SendAsync("getstatusapi"));
        Assert.Equal("lockstatus:busy", await b.SendAsync("getlockstatus"));
        Assert.Equal("busy", await b.SendAsync("setcolor:1-1,2,3"));
        Assert.Equal("busy", await b.SendAsync("setstatus:off"));
        Assert.Equal("busy", await b.SendAsync("setgamma:x")); // lock check comes before validation
        Assert.Equal("unlock:not locked", await b.SendAsync("unlock"));
        Assert.Equal("127.0.0.1", server.Status.LockedBy);
        Assert.Equal(2, server.Status.Clients);

        Assert.Equal("unlock:success", await a.SendAsync("unlock"));
        Assert.False(host.LastControl.Active);
        Assert.Equal("statusapi:idle", await b.SendAsync("getstatusapi"));
        Assert.Equal("lock:success", await b.SendAsync("lock"));
        Assert.Equal("lock:busy", await a.SendAsync("lock"));
    }

    [Fact]
    public async Task Lock_IsReleasedWhenTheClientDisconnects()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host);
        using var b = await ApiClient.ConnectAsync(server.Port);
        var a = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await a.SendAsync("lock"));
        Assert.Equal("lock:busy", await b.SendAsync("lock"));

        a.Dispose();
        await WaitUntil(() => !host.LastControl.Active);
        Assert.Equal("lock:success", await b.SendAsync("lock"));
        await WaitUntil(() => server.Status.Clients == 1);
    }

    [Fact]
    public async Task Lock_FreezesCurrentColors_UntilFirstSetColor()
    {
        var host = new FakePrismatikHost(3);
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.Equal(host.Colors, host.ShownColors);
        Assert.Equal(PrismatikTestKitColors(0, 0, 0, 9, 9, 9, 0, 0, 0), await SetAndShow(c, host, "setcolor:2-9,9,9"));
    }

    private static byte[] PrismatikTestKitColors(params int[] v) => v.Select(x => (byte)x).ToArray();

    private static async Task<byte[]> SetAndShow(ApiClient c, FakePrismatikHost host, string cmd)
    {
        Assert.Equal("ok", await c.SendAsync(cmd));
        return host.ShownColors!;
    }

    // ------------------------------------------------------------------ setcolor

    [Fact]
    public async Task SetColor_OneBased_Multiple_KeepsUnmentionedLeds()
    {
        var host = new FakePrismatikHost(4);
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));

        Assert.Equal(PrismatikTestKitColors(255, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0),
            await SetAndShow(c, host, "setcolor:1-255,0,0;3-0,0,255;"));
        Assert.Equal(PrismatikTestKitColors(255, 0, 0, 0, 0, 0, 0, 0, 255, 1, 2, 3),
            await SetAndShow(c, host, "setcolor:4-1,2,3")); // trailing ';' optional; others keep their API color
        Assert.Equal(PrismatikTestKitColors(255, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255),
            await SetAndShow(c, host, "setcolor:4-255,255,255;4-255,255,255"));

        // the override renders them as 0..1 floats for the LED-correction path
        var ov = Assert.IsType<ApiColorOverride>(host.LastControl.Override);
        Assert.Equal(RambiBox.Core.Engine.OverrideColorPath.LedCorrection, ((RambiBox.Core.Engine.ILedOverride)ov).ColorPath);
        var f = new float[12];
        ov.Render(f, 4, 0);
        Assert.Equal(1f, f[0]);
        Assert.Equal(0f, f[1]);
        Assert.Equal(1f, f[11]);

        // a new lock session continues from the last API colors
        Assert.Equal("unlock:success", await c.SendAsync("unlock"));
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.Equal(PrismatikTestKitColors(255, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0)[..12],
            await SetAndShow(c, host, "setcolor:2-0,0,0"));
    }

    [Theory]
    [InlineData("setcolor:0-1,2,3")]        // LED numbers are 1-based
    [InlineData("setcolor:5-1,2,3")]        // beyond the LED count (4)
    [InlineData("setcolor:01-1,2,3")]       // no leading zero
    [InlineData("setcolor:1-256,0,0")]      // > 255 is rejected, not clamped (Prismatik)
    [InlineData("setcolor:1-1,2")]          // missing component
    [InlineData("setcolor:1-1,2,3,4")]      // extra component
    [InlineData("setcolor:1-1,,3")]
    [InlineData("setcolor:1-a,2,3")]
    [InlineData("setcolor: 1-1,2,3")]       // no spaces
    [InlineData("setcolor:1-1,2,3;;")]
    [InlineData("setcolor:1-1,2,3;2-")]
    [InlineData("setcolor:1-1,2,-3")]
    [InlineData("setcolor:1:1,2,3")]
    [InlineData("setcolor:-1,2,3")]
    public async Task SetColor_Malformed_IsErrorAndChangesNothing(string cmd)
    {
        var host = new FakePrismatikHost(4);
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        var before = await SetAndShow(c, host, "setcolor:1-10,20,30;2-40,50,60;3-70,80,90;4-100,110,120");
        Assert.Equal("error", await c.SendAsync(cmd));
        Assert.Equal(before, host.ShownColors);
    }

    [Fact]
    public void SetColorParser_Limits()
    {
        var list = new List<PrismatikProtocol.LedColor>();
        Assert.True(PrismatikProtocol.TryParseSetColor("1-0,0,0;2-255,255,255", 2, list));
        Assert.Equal(new PrismatikProtocol.LedColor[] { new(0, 0, 0, 0), new(1, 255, 255, 255) }, list);
        Assert.True(PrismatikProtocol.TryParseSetColor("", 2, list)); // empty: ok, nothing (Prismatik)
        Assert.Empty(list);
        Assert.True(PrismatikProtocol.TryParseSetColor("1000-1,2,3", 1000, list)); // 4-digit LED numbers
        Assert.Equal(999, list[0].Index);
        Assert.False(PrismatikProtocol.TryParseSetColor("10000-1,2,3", 20000, list));
        Assert.False(PrismatikProtocol.TryParseSetColor("1-1,2,3", 0, list));
        Assert.False(PrismatikProtocol.TryParseSetColor("1-0,0,2550", 2, list)); // value grows past 255
        Assert.True(PrismatikProtocol.TryParseSetColor("1-0,0,0255", 2, list)); // leading zeros in values are fine (Prismatik)
        Assert.Equal(255, list[0].B);
    }

    // ------------------------------------------------------------------ other set commands

    [Fact]
    public async Task SetCommands_ChangeTheHost()
    {
        var host = new FakePrismatikHost(10);
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));

        Assert.Equal("ok", await c.SendAsync("setstatus:off"));
        Assert.False(host.Enabled);
        Assert.Equal("status:off", await c.SendAsync("getstatus"));
        Assert.Equal("ok", await c.SendAsync("setstatus:on"));
        Assert.True(host.Enabled);
        Assert.Equal("error", await c.SendAsync("setstatus:ON"));
        Assert.Equal("error", await c.SendAsync("setstatus:"));

        Assert.Equal("ok", await c.SendAsync("setbrightness:50"));
        Assert.Equal(0.5, host.Brightness, 6);
        Assert.Equal("brightness:50", await c.SendAsync("getbrightness"));
        Assert.Equal("ok", await c.SendAsync("setbrightness:0"));
        Assert.Equal(0.0, host.Brightness, 6);
        Assert.Equal("error", await c.SendAsync("setbrightness:101"));
        Assert.Equal("error", await c.SendAsync("setbrightness:-1"));
        Assert.Equal("error", await c.SendAsync("setbrightness:0100")); // more than 3 characters (Prismatik)
        Assert.Equal("error", await c.SendAsync("setbrightness:abc"));
        Assert.Equal(0.0, host.Brightness, 6);

        Assert.Equal("ok", await c.SendAsync("setsmooth:50"));
        Assert.Equal(200, host.SmoothingMs);
        Assert.Equal("smooth:50", await c.SendAsync("getsmooth"));
        Assert.Equal("ok", await c.SendAsync("setsmooth:0"));
        Assert.Equal(0, host.SmoothingMs);
        Assert.Equal("ok", await c.SendAsync("setsmooth:255"));
        Assert.Equal(1000, host.SmoothingMs); // capped at the UI maximum
        Assert.Equal("smooth:250", await c.SendAsync("getsmooth"));
        Assert.Equal("error", await c.SendAsync("setsmooth:256"));

        Assert.Equal("ok", await c.SendAsync("setgamma:2.5"));
        Assert.Equal(2.5, host.Gamma, 6);
        Assert.Equal("gamma:2.5", await c.SendAsync("getgamma"));
        Assert.Equal("error", await c.SendAsync("setgamma:10.01"));
        Assert.Equal("error", await c.SendAsync("setgamma:0"));
        Assert.Equal("error", await c.SendAsync("setgamma:2.0001")); // more than 5 characters
        Assert.Equal("error", await c.SendAsync("setgamma:x"));

        Assert.Equal("ok", await c.SendAsync("setmode:moodlamp"));
        Assert.Equal(LightMode.Mood, host.Mode);
        Assert.Equal("mode:moodlamp", await c.SendAsync("getmode"));
        host.Mode = LightMode.StaticColor;
        Assert.Equal("ok", await c.SendAsync("setmode:moodlamp")); // static color already is a "mood lamp"
        Assert.Equal(LightMode.StaticColor, host.Mode);
        Assert.Equal("ok", await c.SendAsync("setmode:ambilight"));
        Assert.Equal(LightMode.Ambilight, host.Mode);
        Assert.Equal("error", await c.SendAsync("setmode:soundviz"));

        Assert.Equal("ok", await c.SendAsync("setprofile:Default"));
        Assert.Equal("error", await c.SendAsync("setprofile:Movies"));
        Assert.Equal("error", await c.SendAsync("newprofile:Movies"));
        Assert.Equal("error", await c.SendAsync("deleteprofile:Default"));

        Assert.Equal("ok", await c.SendAsync("setpersistonunlock:on"));
        Assert.Equal("persistonunlock:on", await c.SendAsync("getpersistonunlock"));
        Assert.Equal("error", await c.SendAsync("setpersistonunlock:yes"));
    }

    [Fact]
    public async Task SetLeds_ConvertsPixelsToZones_AndSkipsInvalidEntries()
    {
        var host = new FakePrismatikHost(10) { Screen = new PixelRect(-1920, 0, 1920, 1080) };
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.Equal("ok", await c.SendAsync("setleds:2--1920,0,192,108;11-0,0,10,10;x-1,2,3,4;3-0,0,0,5;"));
        var change = Assert.Single(host.Changes);
        var z = Assert.Single(change.Zones!);
        Assert.Equal(new ZoneUpdate(1, 0f, 0f, 0.1f, 0.1f), z);
        Assert.Equal("ok", await c.SendAsync("setleds:garbage"));
        Assert.Single(host.Changes);
        Assert.StartsWith("leds:0-", await c.SendAsync("getleds"));
    }

    [Fact]
    public async Task PersistOnUnlock_KeepsColorsUntilReleased()
    {
        var host = new FakePrismatikHost(2);
        using var server = Start(host);
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.Equal("ok", await c.SendAsync("setpersistonunlock:on"));
        Assert.Equal("ok", await c.SendAsync("setcolor:1-1,2,3;2-4,5,6"));
        Assert.Equal("unlock:success", await c.SendAsync("unlock"));

        var ctl = host.LastControl;
        Assert.True(ctl.Persisting);
        Assert.False(ctl.Locked);
        Assert.Equal(PrismatikTestKitColors(1, 2, 3, 4, 5, 6), host.ShownColors);
        Assert.True(server.Status.Persisting);
        Assert.Equal("statusapi:idle", await c.SendAsync("getstatusapi"));

        // locking again starts from the persisted colors
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.Equal(PrismatikTestKitColors(1, 2, 3, 4, 5, 6), host.ShownColors);
        Assert.Equal("unlock:success", await c.SendAsync("unlock"));

        server.ReleasePersistedColors(); // the user switched the lights / mode
        Assert.False(host.LastControl.Active);

        // disconnecting with "persist" on keeps the colors too
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.Equal(host.Colors, host.ShownColors); // normal output frozen until the first setcolor
        Assert.Equal("ok", await c.SendAsync("setcolor:2-7,8,9"));
        c.Dispose();
        await WaitUntil(() => host.LastControl is { Persisting: true, Locked: false });
        Assert.Equal(PrismatikTestKitColors(1, 2, 3, 7, 8, 9), host.ShownColors);

        server.Stop(); // disabling the API ends everything
        Assert.False(host.LastControl.Active);
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public async Task OverlongLine_IsAnsweredWithError_AndTheSessionContinues()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host, create: () => new PrismatikApiServer(host) { MaxLineLength = 100 });
        using var c = await ApiClient.ConnectAsync(server.Port);
        await c.WriteAsync(new string('x', 5000) + "\n");
        Assert.Equal("error", await c.ReadLineAsync());
        Assert.Equal("status:on", await c.SendAsync("getstatus"));
        Assert.Equal("countleds:10", await c.SendAsync("getcountleds"));
    }

    [Fact]
    public async Task IdleClient_IsDisconnected_AndItsLockReleased()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host, create: () => new PrismatikApiServer(host) { IdleTimeout = TimeSpan.FromMilliseconds(400) });
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.True(await c.WaitClosedAsync(5000));
        await WaitUntil(() => !host.LastControl.Active && server.Status.Clients == 0);
    }

    [Fact]
    public async Task MaxClients_ExtraConnectionsAreClosed()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host, create: () => new PrismatikApiServer(host) { MaxClients = 2 });
        using var a = await ApiClient.ConnectAsync(server.Port);
        using var b = await ApiClient.ConnectAsync(server.Port);
        using var c = await ApiClient.ConnectAsync(server.Port, readGreeting: false);
        Assert.True(await c.WaitClosedAsync());
        Assert.Equal("status:on", await a.SendAsync("getstatus"));
        b.Dispose();
        await WaitUntil(() => server.Status.Clients == 1);
        using var d = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("status:on", await d.SendAsync("getstatus"));
    }

    [Fact]
    public async Task HostFailures_DoNotBreakTheSession()
    {
        var host = new ThrowingHost();
        using var server = Start(new FakePrismatikHost(), create: () => new PrismatikApiServer(host));
        using var c = await ApiClient.ConnectAsync(server.Port);
        Assert.Equal("error", await c.SendAsync("getstatus"));
        Assert.Equal("lock:success", await c.SendAsync("lock"));
        Assert.Equal("error", await c.SendAsync("setbrightness:10"));
        Assert.Equal("profile:Default", await c.SendAsync("getprofile"));
    }

    private sealed class ThrowingHost : IPrismatikHost
    {
        public PrismatikHostState GetState() => throw new InvalidOperationException("boom");
        public byte[] GetColors() => throw new InvalidOperationException("boom");
        public IReadOnlyList<PixelRect> GetMonitors() => throw new InvalidOperationException("boom");
        public Task<bool> ApplyAsync(PrismatikChange change) => Task.FromException<bool>(new InvalidOperationException("boom"));
        public void OnApiControlChanged(PrismatikControl control) => throw new InvalidOperationException("boom");
    }

    // ------------------------------------------------------------------ security / lifecycle

    [Theory]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    [InlineData("192.168.2.10", false, false)]
    [InlineData("192.168.2.10", true, true)]
    [InlineData("10.1.2.3", true, true)]
    [InlineData("172.16.0.5", true, true)]
    [InlineData("172.32.0.5", true, false)]
    [InlineData("8.8.8.8", true, false)]
    [InlineData("100.64.1.1", true, false)]
    [InlineData("::ffff:192.168.1.7", true, true)]
    [InlineData("::ffff:8.8.8.8", true, false)]
    [InlineData("2001:db8::1", true, false)]
    public void LanRestriction(string ip, bool allowLan, bool allowed) =>
        Assert.Equal(allowed, PrismatikApiServer.IsClientAllowed(IPAddress.Parse(ip), allowLan));

    [Fact]
    public async Task NonLocalClient_IsRefused_WhenLanIsOff()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host, create: () => new PrismatikApiServer(host) { ClientAddress = _ => IPAddress.Parse("192.168.2.10") });
        using var c = await ApiClient.ConnectAsync(server.Port, readGreeting: false);
        Assert.True(await c.WaitClosedAsync());
        Assert.Equal(0, server.Status.Clients);
    }

    [Fact]
    public async Task LoopbackOnly_ByDefault_AndStatusShowsTheEndpoint()
    {
        var host = new FakePrismatikHost();
        using var server = Start(host);
        Assert.Equal($"127.0.0.1:{server.Port}", server.Status.Endpoint);
        Assert.Null(server.Status.Error);

        using var c = await ApiClient.ConnectAsync(server.Port);
        await WaitUntil(() => server.Status.Clients == 1);

        server.Stop();
        Assert.False(server.Status.Listening);
        Assert.True(await c.WaitClosedAsync());
    }

    [Fact]
    public void PortInUse_IsReportedNotThrown()
    {
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            using var server = new PrismatikApiServer(new FakePrismatikHost());
            server.Apply(new ApiSettings { Enabled = true, Port = port });
            Assert.False(server.Status.Listening);
            Assert.Contains($"Port {port}", server.Status.Error);
        }
        finally { blocker.Stop(); }
    }

    [Fact]
    public void ApiSettings_Normalize()
    {
        var a = new ApiSettings { Port = 0, ApiKey = "  key\twith\u0001ctrl  " };
        ApiSettings.Normalize(a);
        Assert.Equal(3636, a.Port);
        Assert.Equal("keywithctrl", a.ApiKey);
        Assert.Equal(32, ApiSettings.GenerateKey().Length);

        var s = new AppSettings();
        Assert.False(s.Api.Enabled);
        Assert.Equal(3636, s.Api.Port);
        Assert.False(s.Api.AllowLan);
        s.Api = null!;
        SettingsSerializer.Normalize(s);
        Assert.NotNull(s.Api);
        var round = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(new AppSettings { Api = new ApiSettings { Enabled = true, Port = 4000, ApiKey = "k" } }));
        Assert.True(round.Api.Enabled);
        Assert.Equal(4000, round.Api.Port);
        Assert.Equal("k", round.Api.ApiKey);
    }

    [Fact]
    public void SmoothAndBrightnessMappings()
    {
        Assert.Equal(0, PrismatikProtocol.SmoothToMs(0));
        Assert.Equal(100, PrismatikProtocol.SmoothToMs(25));
        Assert.Equal(1000, PrismatikProtocol.SmoothToMs(255));
        Assert.Equal(25, PrismatikProtocol.MsToSmooth(100));
        Assert.Equal(255, PrismatikProtocol.MsToSmooth(2000));
        Assert.Equal(64, PrismatikProtocol.BrightnessToApi(0.636));
        Assert.Equal("33.6", PrismatikProtocol.FormatDouble(33.6));
        Assert.Equal("1.2", PrismatikProtocol.FormatDouble(1.2));
        Assert.Equal("25.5734", PrismatikProtocol.FormatDouble(25.573412));
    }
}
