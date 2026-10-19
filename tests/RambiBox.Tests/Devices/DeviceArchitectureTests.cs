using RambiBox.Core.Config;
using RambiBox.Core.Devices;

namespace RambiBox.Tests.Devices;

public class DeviceArchitectureTests
{
    private sealed class RecordingDevice(DeviceType type, List<string> log) : ILedDevice
    {
        public DeviceType Type { get; } = type;
        public DeviceStatus Status => DeviceStatus.Connected;
        public string? PortName => Type.ToString();
        public event Action? StatusChanged { add { } remove { } }
        public event Action<SerialPortInfo>? DeviceFound { add { } remove { } }
        public void Configure(DeviceSettings settings, int ledCount) => log.Add($"{Type}:configure:{ledCount}");
        public void Submit(ReadOnlySpan<byte> rgb) => log.Add($"{Type}:submit:{rgb.Length}");
        public bool WaitForSlot(int timeoutMs) => true;
        public double MaxFrameRate => 30;
        public double FramesPerSecond => 0;
        public void Rescan() { }
        public void Blackout() => log.Add($"{Type}:blackout");
        public IReadOnlyList<SerialPortInfo> ListPorts() => [];
        public void Dispose() => log.Add($"{Type}:dispose");
    }

    [Fact]
    public void Switching_device_swaps_driver_when_type_changes_and_blacks_out_the_old_one()
    {
        var log = new List<string>();
        using var dev = new SwitchingLedDevice(t => new RecordingDevice(t, log));
        var s = new DeviceSettings { Type = DeviceType.Adalight };
        dev.Configure(s, 10);
        dev.Submit(new byte[30]);
        dev.Configure(s, 12); // same type: reconfigure only
        s.Type = DeviceType.Wled;
        dev.Configure(s, 12);
        dev.Submit(new byte[36]);
        Assert.Equal(
            ["Adalight:configure:10", "Adalight:submit:30", "Adalight:configure:12",
             "Adalight:blackout", "Adalight:dispose", "Wled:configure:12", "Wled:submit:36"], log);
        Assert.Equal(DeviceType.Wled, dev.ActiveType);
        Assert.Equal("Wled", dev.PortName);
    }

    [Fact]
    public void Factory_knows_the_built_in_drivers_and_falls_back_to_virtual()
    {
        Assert.True(LedDeviceFactory.IsSupported(DeviceType.Adalight));
        Assert.True(LedDeviceFactory.IsSupported(DeviceType.Virtual));
        using var d = LedDeviceFactory.Create(DeviceType.Virtual);
        Assert.IsType<VirtualLedDevice>(d);
    }

    [Fact]
    public void Virtual_device_keeps_the_last_frame_and_paces()
    {
        using var d = new VirtualLedDevice();
        Assert.False(d.WaitForSlot(10));
        d.Configure(new DeviceSettings(), 2);
        Assert.Equal(DeviceStatus.Connected, d.Status);
        Assert.True(d.WaitForSlot(100));
        d.Submit(new byte[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, d.LastFrame);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 6; i++) Assert.True(d.WaitForSlot(100));
        Assert.InRange(sw.ElapsedMilliseconds, 60, 400); // ~60 fps pacing
    }

    [Fact]
    public void Unknown_device_type_in_settings_reads_as_adalight_without_losing_the_file()
    {
        var s = SettingsSerializer.Deserialize("""{ "device": { "type": "QuantumLaser", "protocol": "Nope", "port": "COM7", "wledTimeoutSeconds": 999 } }""");
        Assert.Equal(DeviceType.Adalight, s.Device.Type);
        Assert.Equal(NetworkProtocol.Auto, s.Device.Protocol);
        Assert.Equal("COM7", s.Device.Port);
        Assert.Equal(255, s.Device.WledTimeoutSeconds);
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(new AppSettings { Device = { Type = DeviceType.E131, Host = " 10.0.0.5 " } }));
        Assert.Equal(DeviceType.E131, back.Device.Type);
        Assert.Equal("10.0.0.5", back.Device.Host);
    }
}
