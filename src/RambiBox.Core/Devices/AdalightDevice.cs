using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// Adalight LED controller on a (USB-)serial port ("Ada" header, see <see cref="AdalightProtocol"/>).
/// The firmware greets with "Ada\n" after the reset caused by opening the port, which makes auto-detect reliable.
/// All connection handling lives in <see cref="SerialLedDevice"/>.
/// </summary>
[LedDevice(DeviceType.Adalight)]
public sealed class AdalightDevice : SerialLedDevice
{
    public AdalightDevice() : this(Win32PortEnumerator.Shared, new Win32SerialLinkFactory(), AdalightOptions.Default) { }

    internal AdalightDevice(IPortEnumerator ports, ISerialLinkFactory links, AdalightOptions? options = null)
        : base(AdalightProtocol.Instance, ports, links, options) { }
}
