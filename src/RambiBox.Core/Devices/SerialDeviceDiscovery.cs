using System.Diagnostics;
using RambiBox.Core.Config;

namespace RambiBox.Core.Devices;

/// <summary>
/// Lists serial LED controllers (Adalight, Ardulight, AWA, TPM2) without disturbing devices in use.
/// <list type="bullet">
/// <item>Every present USB-serial port is reported (known Arduino/USB-serial chips first). Bluetooth and modem ports are
/// never reported or opened; ports without a USB id (built-in COM1 …) only with <see cref="DeepProbe"/>.</item>
/// <item>Ports of known chips are probed for the Adalight greeting ("Ada" after the reset caused by opening) in
/// parallel. A port somebody else has open cannot be opened (exclusive access) and is reported as in use - nothing is
/// written to it, nothing is reset. Ports of unknown chips (a 3D printer's own USB id, …) are only probed with
/// <see cref="DeepProbe"/>, because opening them resets many Arduino-based devices.</item>
/// <item>Only the "Ada" greeting identifies a firmware passively; Ardulight and TPM2 never identify themselves and the
/// AWA hello needs 2 Mbaud and a write, so a port without greeting is reported as an unverified serial controller
/// (type Adalight, see <see cref="DiscoveredDevice.Detail"/>), letting the user pick the protocol.</item>
/// </list>
/// The whole scan takes at most <see cref="AdalightOptions.ProbeTimeoutMs"/> (2.5 s) or the caller's timeout, whichever is
/// shorter, and honours cancellation.
/// </summary>
public sealed class SerialDeviceDiscovery : IDeviceDiscovery
{
    private const int ProbeBaud = 115200; // Adalight firmware default
    private const int MinProbeMs = 300;   // below this a probe can't see a greeting; then ports are only listed
    private static readonly long Freq = Stopwatch.Frequency;

    private readonly IPortEnumerator _ports;
    private readonly ISerialLinkFactory _links;
    private readonly AdalightOptions _opt;

    public SerialDeviceDiscovery() : this(deepProbe: false) { }

    /// <param name="deepProbe">Also open ports of unknown USB devices and ports without USB id (may reset them).</param>
    public SerialDeviceDiscovery(bool deepProbe)
        : this(Win32PortEnumerator.Shared, new Win32SerialLinkFactory(), deepProbe, AdalightOptions.Default) { }

    internal SerialDeviceDiscovery(IPortEnumerator ports, ISerialLinkFactory links, bool deepProbe, AdalightOptions? options = null)
    {
        _ports = ports;
        _links = links;
        DeepProbe = deepProbe;
        _opt = options ?? AdalightOptions.Default;
    }

    /// <summary>Probe ports of unknown chips / without USB id too.</summary>
    public bool DeepProbe { get; }

    public IReadOnlyList<DeviceType> Types { get; } = [DeviceType.Adalight, DeviceType.Ardulight, DeviceType.Awa, DeviceType.Tpm2];

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        List<SerialPortInfo> ports;
        bool deep = DeepProbe;
        try
        {
            var present = new HashSet<string>(_ports.GetPresentPortNames(), StringComparer.OrdinalIgnoreCase);
            var modemList = SafeModems();
            if (modemList is null) deep = false; // unknown modem set: only known USB-serial chips are opened
            var modems = new HashSet<string>(modemList ?? [], StringComparer.OrdinalIgnoreCase);
            ports = _ports.ListPorts()
                .Where(p => present.Contains(p.PortName) && !p.IsBluetooth && !modems.Contains(p.PortName))
                .Where(p => deep || p.UsbId is not null)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Serial discovery: listing ports failed: {ex.Message}");
            return Array.Empty<DiscoveredDevice>();
        }
        if (ports.Count == 0) return Array.Empty<DiscoveredDevice>();

        var results = ports.ToDictionary(p => p.PortName, _ => PortResult.NotProbed, StringComparer.OrdinalIgnoreCase);
        int budgetMs = (int)Math.Min(_opt.ProbeTimeoutMs, Math.Max(0, timeout.TotalMilliseconds - 100));
        if (budgetMs >= MinProbeMs && !ct.IsCancellationRequested)
        {
            long deadline = Stopwatch.GetTimestamp() + budgetMs * Freq / 1000;
            var toProbe = ports.Where(p => deep || SerialLedDevice.IsKnownChip(p.UsbId)).ToList();
            var tasks = toProbe.Select(p => Task.Factory.StartNew(
                () => (p.PortName, Result: ProbePort(p.PortName, deadline, ct)),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToList();
            try
            {
                foreach (var (port, result) in await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false))
                    results[port] = result;
            }
            catch (OperationCanceledException)
            {
                // Cancelled: report what is known; the probes stop within ~50 ms and close their ports.
                foreach (var t in tasks.Where(t => t.IsCompletedSuccessfully)) results[t.Result.PortName] = t.Result.Result;
            }
        }

        return ports
            .Select(p => (Info: p, Result: results[p.PortName]))
            .OrderBy(x => x.Result == PortResult.Adalight ? 0 : SerialLedDevice.IsKnownChip(x.Info.UsbId) ? 1 : 2)
            .ThenBy(x => x.Info.PortName, Comparer<string>.Create(Win32PortEnumerator.ComparePortNames))
            .Select(x => ToDevice(x.Info, x.Result))
            .ToList();
    }

    private PortResult ProbePort(string port, long deadline, CancellationToken ct)
    {
        bool Interrupted() => ct.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline;
        ISerialLink? link;
        try
        {
            link = SerialOpenGate.Open(_links, port, ProbeBaud, _opt.MinReopenIntervalMs, Interrupted);
            if (link is null) return PortResult.NotProbed;
        }
        catch (UnauthorizedAccessException)
        {
            return PortResult.InUse; // somebody has it open: nothing was disturbed
        }
        catch (Exception)
        {
            return PortResult.OpenFailed;
        }

        try
        {
            int remainingMs = (int)Math.Max(0, (deadline - Stopwatch.GetTimestamp()) * 1000 / Freq);
            var g = SerialOpenGate.WaitForGreeting(link, AdalightProtocol.Instance.CreateGreeting(), remainingMs,
                () => ct.IsCancellationRequested, out bool anyBytes);
            return g switch
            {
                GreetingResult.Greeted => PortResult.Adalight,
                GreetingResult.Interrupted => PortResult.NotProbed,
                _ => anyBytes ? PortResult.OtherData : PortResult.Silent,
            };
        }
        catch (Exception)
        {
            return PortResult.OpenFailed;
        }
        finally
        {
            try { link.Dispose(); } catch { /* closing after surprise removal may throw */ }
        }
    }

    private static DiscoveredDevice ToDevice(SerialPortInfo p, PortResult r)
    {
        string chip = p.UsbId is null ? p.Description : $"{p.Description} ({p.UsbId})";
        string detail = r switch
        {
            PortResult.Adalight => $"{chip} · Adalight firmware answered",
            PortResult.InUse => $"{chip} · in use by a program (possibly RambiBox itself); not probed",
            PortResult.Silent => $"{chip} · no Adalight greeting: Ardulight, AWA or TPM2 controller, or another device",
            PortResult.OtherData => $"{chip} · sends other data: AWA/other firmware or a different baud rate",
            PortResult.OpenFailed => $"{chip} · could not be opened",
            _ => SerialLedDevice.IsKnownChip(p.UsbId)
                ? $"{chip} · USB-serial adapter; not probed"
                : $"{chip} · unknown USB device; not probed (opening it could reset it)",
        };
        string name = r == PortResult.Adalight ? $"Adalight on {p.PortName}" : $"Serial controller on {p.PortName}";
        return new DiscoveredDevice(DeviceType.Adalight, p.PortName, name, detail, Port: p.PortName);
    }

    /// <summary>Modem-class ports, or null when they cannot be determined.</summary>
    private IReadOnlyCollection<string>? SafeModems()
    {
        try { return _ports.GetModemPortNames(); }
        catch (Exception ex)
        {
            Log.Warn($"Serial discovery: listing modem ports failed: {ex.Message}");
            return null;
        }
    }

    private enum PortResult { NotProbed, Adalight, Silent, OtherData, InUse, OpenFailed }
}
