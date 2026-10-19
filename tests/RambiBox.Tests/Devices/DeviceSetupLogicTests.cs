using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

/// <summary>Pure logic behind the device page: validation, auto-switch decisions, type catalogue and status texts.</summary>
public class DeviceSetupLogicTests
{
    // ------------------------------------------------------------------ host / port validation

    [Theory]
    [InlineData("192.168.2.40", "192.168.2.40")]
    [InlineData("  10.0.0.1 ", "10.0.0.1")]
    [InlineData("wled-tv.local", "wled-tv.local")]
    [InlineData("WLED-TV", "WLED-TV")]
    [InlineData("http://192.168.2.40/", "192.168.2.40")]
    [InlineData("http://wled-tv.local:80/settings", "wled-tv.local")]
    [InlineData("fe80::1", "fe80::1")]
    public void Valid_hosts_are_accepted_and_cleaned_up(string input, string expected)
    {
        Assert.Null(DeviceValidation.ValidateHost(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("192.168.1.300")]
    [InlineData("192.168.1")]
    [InlineData("192.168..1")]
    [InlineData("0.0.0.0")]
    [InlineData("wled tv")]
    [InlineData("-wled.local")]
    [InlineData("wled_tv")]
    [InlineData("192.168.2.40:21324")]
    public void Invalid_hosts_get_a_friendly_message(string? input)
    {
        var error = DeviceValidation.ValidateHost(input, out _);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("21324", 0)] // the default is stored as 0
    [InlineData("4048", 4048)]
    [InlineData(" 65535 ", 65535)]
    public void Valid_ports(string input, int expected)
    {
        Assert.Null(DeviceValidation.ValidatePort(input, DeviceCatalog.WledDefaultPort, out int port));
        Assert.Equal(expected, port);
    }

    [Theory]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("12.5")]
    public void Invalid_ports_mention_the_range_and_default(string input)
    {
        var error = DeviceValidation.ValidatePort(input, DeviceCatalog.DdpDefaultPort, out _);
        Assert.NotNull(error);
        Assert.Contains("65535", error);
        Assert.Contains("4048", error);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("63999", true)]
    [InlineData("0", false)]
    [InlineData("64000", false)]
    [InlineData("x", false)]
    public void Universe_range(string input, bool ok) => Assert.Equal(ok, DeviceValidation.ValidateUniverse(input, out _) is null);

    // ------------------------------------------------------------------ auto-switch decision

    private static DiscoveredDevice Wled(string host = "192.168.2.40", string id = "AABBCCDDEEFF") =>
        new(DeviceType.Wled, id, $"WLED-TV ({host})", "WLED 0.15", 120, Host: host);

    private static DiscoveredDevice Serial(DeviceType t, string port) => new(t, port, $"{t} on {port}", "CH340", Port: port);

    private static readonly Func<DeviceType, bool> AllSupported = _ => true;

    [Fact]
    public void Exactly_one_device_on_untouched_settings_switches_automatically()
    {
        var d = DeviceAutoSwitchPolicy.Decide(new DeviceSettings(), firstRunWithoutImport: false, [Wled()], AllSupported);
        Assert.Equal(AutoSwitchAction.Switch, d.Action);
        Assert.Equal(DeviceType.Wled, d.Device!.Type);
    }

    [Fact]
    public void First_run_without_import_switches_automatically()
    {
        var s = new DeviceSettings { LastPort = "COM9" }; // e.g. a leftover from a simulated run
        Assert.Equal(AutoSwitchAction.Switch,
            DeviceAutoSwitchPolicy.Decide(s, firstRunWithoutImport: true, [Wled()], AllSupported).Action);
    }

    [Fact]
    public void A_device_the_user_configured_is_never_switched_away_from()
    {
        // Adalight that worked before (remembered port) but is unplugged right now
        var adalight = new DeviceSettings { LastPort = "COM3", LastUsbId = "1A86:7523" };
        Assert.Equal(AutoSwitchAction.Suggest, DeviceAutoSwitchPolicy.Decide(adalight, false, [Wled()], AllSupported).Action);

        // a fixed COM port
        var fixedPort = new DeviceSettings { Port = "COM5" };
        Assert.Equal(AutoSwitchAction.Suggest, DeviceAutoSwitchPolicy.Decide(fixedPort, false, [Wled()], AllSupported).Action);

        // a configured network device that is offline
        var ddp = new DeviceSettings { Type = DeviceType.Ddp, Host = "192.168.2.50" };
        Assert.Equal(AutoSwitchAction.Suggest, DeviceAutoSwitchPolicy.Decide(ddp, false, [Wled()], AllSupported).Action);
    }

    [Fact]
    public void Several_devices_ask_the_user_to_choose()
    {
        var d = DeviceAutoSwitchPolicy.Decide(new DeviceSettings(), true,
            [Wled(), Wled("192.168.2.41", "112233445566"), Serial(DeviceType.Awa, "COM7")], AllSupported);
        Assert.Equal(AutoSwitchAction.Choose, d.Action);
        Assert.Equal(3, d.Candidates.Count);
        Assert.Null(d.Device);
    }

    [Fact]
    public void Nothing_found_does_nothing() =>
        Assert.Equal(AutoSwitchAction.None, DeviceAutoSwitchPolicy.Decide(new DeviceSettings(), true, [], AllSupported).Action);

    [Fact]
    public void The_current_device_and_duplicates_are_not_candidates()
    {
        // Adalight on "Automatic" already looks at every port itself
        var auto = new DeviceSettings();
        Assert.Equal(AutoSwitchAction.None,
            DeviceAutoSwitchPolicy.Decide(auto, true, [Serial(DeviceType.Adalight, "COM4")], AllSupported).Action);

        // the configured WLED, reported twice (two services / interfaces)
        var wled = new DeviceSettings { Type = DeviceType.Wled, Host = "192.168.2.40" };
        Assert.Equal(AutoSwitchAction.None, DeviceAutoSwitchPolicy.Decide(wled, false, [Wled(), Wled()], AllSupported).Action);

        // the same device twice counts once
        var d = DeviceAutoSwitchPolicy.Decide(auto, true, [Wled(), Wled() with { DisplayName = "dup" }], AllSupported);
        Assert.Equal(AutoSwitchAction.Switch, d.Action);
    }

    [Fact]
    public void Types_without_a_driver_are_ignored()
    {
        var d = DeviceAutoSwitchPolicy.Decide(new DeviceSettings(), true, [Wled()], t => t != DeviceType.Wled);
        Assert.Equal(AutoSwitchAction.None, d.Action);
    }

    [Fact]
    public void Untouched_default_detection()
    {
        Assert.True(DeviceAutoSwitchPolicy.IsUntouchedDefault(new DeviceSettings()));
        Assert.False(DeviceAutoSwitchPolicy.IsUntouchedDefault(new DeviceSettings { Type = DeviceType.Virtual }));
        Assert.False(DeviceAutoSwitchPolicy.IsUntouchedDefault(new DeviceSettings { LastUsbId = "1A86:7523" }));
        Assert.False(DeviceAutoSwitchPolicy.IsUntouchedDefault(new DeviceSettings { Port = "COM3" }));
    }

    // ------------------------------------------------------------------ catalogue / texts

    [Fact]
    public void Catalogue_lists_every_device_type_once()
    {
        Assert.Equal(Enum.GetValues<DeviceType>().Order(), DeviceCatalog.All.Select(i => i.Type).Order());
        Assert.All(DeviceCatalog.All, i => Assert.False(string.IsNullOrWhiteSpace(i.Description)));
    }

    [Theory]
    [InlineData(DeviceType.Adalight, 115200, DeviceType.Awa, 2000000)] // untouched default follows the protocol
    [InlineData(DeviceType.Awa, 2000000, DeviceType.Adalight, 115200)]
    [InlineData(DeviceType.Adalight, 500000, DeviceType.Awa, 500000)] // hand-picked rate is kept
    [InlineData(DeviceType.Wled, 115200, DeviceType.Awa, 2000000)] // coming from a network device
    [InlineData(DeviceType.Adalight, 230400, DeviceType.Wled, 230400)] // not serial: untouched
    public void Baud_follows_the_protocol_default(DeviceType from, int baud, DeviceType to, int expected) =>
        Assert.Equal(expected, DeviceCatalog.BaudAfterTypeChange(from, to, baud));

    [Fact]
    public void Status_texts_describe_each_family()
    {
        var serial = new DeviceSettings();
        Assert.Equal("Connected on COM3", DeviceCatalog.ConnectedText(serial, "COM3"));
        Assert.Equal("Looking for the LED controller…", DeviceCatalog.SearchingText(serial));

        var wled = new DeviceSettings { Type = DeviceType.Wled, Host = "192.168.2.40" };
        Assert.Equal("Connected to 192.168.2.40", DeviceCatalog.ConnectedText(wled, null));
        Assert.Equal("Looking for WLED at 192.168.2.40…", DeviceCatalog.SearchingText(wled));
        wled.Name = "WLED-TV (192.168.2.40)";
        Assert.Equal("Connected to WLED-TV (192.168.2.40)", DeviceCatalog.ConnectedText(wled, "192.168.2.40"));

        var noHost = new DeviceSettings { Type = DeviceType.Ddp };
        Assert.Equal("Set the address of your DDP controller", DeviceCatalog.SearchingText(noHost));

        var multicast = new DeviceSettings { Type = DeviceType.E131, E131Multicast = true };
        Assert.Equal("Connected to sACN multicast", DeviceCatalog.ConnectedText(multicast, null));

        Assert.Equal("Virtual device (preview only)", DeviceCatalog.ConnectedText(new DeviceSettings { Type = DeviceType.Virtual }, "Virtual"));
    }

    [Theory]
    [InlineData(1, 100, "universe 1")]
    [InlineData(1, 170, "universe 1")]
    [InlineData(1, 171, "universes 1–2")]
    [InlineData(5, 600, "universes 5–8")]
    public void E131_universe_range(int start, int leds, string expected) =>
        Assert.Equal(expected, DeviceCatalog.E131UniverseRange(start, leds));
}
