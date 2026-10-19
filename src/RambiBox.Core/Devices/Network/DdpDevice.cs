using RambiBox.Core.Config;

namespace RambiBox.Core.Devices.Network;

/// <summary>
/// DDP (Distributed Display Protocol) over UDP, port 4048 — WLED, ESPixelStick, xLights/Falcon controllers…
/// See <see cref="DdpPacketizer"/>. DDP has no handshake, so the device is Connected as soon as the host resolves and
/// the socket is open. Receivers such as WLED leave realtime mode on their own after their realtime timeout
/// (WLED: 2.5 s by default); on stop RambiBox sends a black frame.
/// </summary>
[LedDevice(DeviceType.Ddp)]
public sealed class DdpDevice : UdpLedDevice
{
    public DdpDevice() : base("DDP") { }

    private protected override int DefaultPort => DdpPacketizer.DefaultPort;

    private protected override UdpPacketizer CreatePacketizer(NetSettings s) => new DdpPacketizer(s.LedCount, s.Order);
}
