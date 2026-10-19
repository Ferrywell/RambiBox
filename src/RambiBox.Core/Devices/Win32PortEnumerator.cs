using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RambiBox.Core.Devices;

/// <summary>
/// Serial port enumeration without opening anything.
/// Presence: HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (volatile key, exactly the ports that exist right now; ~20 µs).
/// Details (description, USB VID:PID, Bluetooth): SetupAPI over the present devices of the Ports and Modem classes,
/// falling back to HKLM\SYSTEM\CurrentControlSet\Enum (USB / FTDIBUS / BTHENUM ...\Device Parameters\PortName).
/// The detailed list is cached until the SERIALCOMM contents change (or 10 s pass).
/// </summary>
internal sealed partial class Win32PortEnumerator : IPortEnumerator
{
    public static Win32PortEnumerator Shared { get; } = new();

    private static readonly Guid GuidDevClassPorts = new("4d36e978-e325-11ce-bfc1-08002be10318");
    private static readonly Guid GuidDevClassModem = new("4d36e96d-e325-11ce-bfc1-08002be10318");
    private const long CacheTtlMs = 10_000;

    private readonly object _lock = new();
    private string? _cacheKey;
    private long _cacheTime;
    private IReadOnlyList<SerialPortInfo> _cache = Array.Empty<SerialPortInfo>();

    public IReadOnlyCollection<string> GetPresentPortNames()
    {
        var map = ReadSerialComm();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, port) in map) set.Add(port);
        return set;
    }

    public IReadOnlyList<SerialPortInfo> ListPorts()
    {
        var present = ReadSerialComm();
        string key = string.Join("|", present.Select(p => p.Device + "=" + p.Port));
        lock (_lock)
        {
            if (key == _cacheKey && Environment.TickCount64 - _cacheTime < CacheTtlMs) return _cache;
        }

        var details = new Dictionary<string, (string Desc, string Instance)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            QuerySetupApi(GuidDevClassPorts, details);
            QuerySetupApi(GuidDevClassModem, details);
        }
        catch (Exception ex)
        {
            Log.Warn($"SetupAPI port enumeration failed: {ex.Message}");
        }
        if (present.Any(p => !details.ContainsKey(p.Port)))
        {
            try { QueryEnumRegistry(details, present.Select(p => p.Port).Where(p => !details.ContainsKey(p)).ToHashSet(StringComparer.OrdinalIgnoreCase)); }
            catch (Exception ex) { Log.Warn($"Registry port enumeration failed: {ex.Message}"); }
        }

        var list = new List<SerialPortInfo>(present.Count);
        foreach (var (device, port) in present)
        {
            if (list.Any(p => p.PortName.Equals(port, StringComparison.OrdinalIgnoreCase))) continue;
            string desc = port, instance = "";
            if (details.TryGetValue(port, out var d)) { desc = d.Desc; instance = d.Instance; }
            else if (!string.IsNullOrEmpty(device)) desc = device.Replace(@"\Device\", "");
            bool bt = instance.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)
                      || instance.StartsWith(@"BTHLEDEVICE\", StringComparison.OrdinalIgnoreCase)
                      || device.Contains("Bth", StringComparison.OrdinalIgnoreCase);
            list.Add(new SerialPortInfo(port, desc, bt ? null : ParseUsbId(instance)) { IsBluetooth = bt });
        }
        list.Sort((a, b) => ComparePortNames(a.PortName, b.PortName));

        lock (_lock)
        {
            _cache = list;
            _cacheKey = key;
            _cacheTime = Environment.TickCount64;
        }
        return list;
    }

    /// <summary>"COM3" &lt; "COM10" (numeric), anything else ordinal.</summary>
    internal static int ComparePortNames(string a, string b)
    {
        int na = PortNumber(a), nb = PortNumber(b);
        if (na >= 0 && nb >= 0 && na != nb) return na.CompareTo(nb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    internal static int PortNumber(string port)
        => port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(port.AsSpan(3), out int n) ? n : -1;

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})[&+]PID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();

    /// <summary>"USB\VID_1A86&amp;PID_7523\..." / "FTDIBUS\VID_0403+PID_6001+..." → "1A86:7523"; null when not found.</summary>
    internal static string? ParseUsbId(string? instanceOrHardwareId)
    {
        if (string.IsNullOrEmpty(instanceOrHardwareId)) return null;
        var m = VidPidRegex().Match(instanceOrHardwareId);
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : null;
    }

    /// <summary>"USB-SERIAL CH340 (COM3)" → "USB-SERIAL CH340"; "@oem49.inf,%x%;USB-SERIAL CH340" → "USB-SERIAL CH340".</summary>
    internal static string CleanDescription(string? text, string port)
    {
        if (string.IsNullOrWhiteSpace(text)) return port;
        int semi = text.LastIndexOf(';');
        if (text.StartsWith('@') && semi >= 0) text = text[(semi + 1)..];
        string suffix = $" ({port})";
        if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) text = text[..^suffix.Length];
        return text.Trim();
    }

    private static List<(string Device, string Port)> ReadSerialComm()
    {
        var result = new List<(string, string)>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null) return result;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string port && !string.IsNullOrWhiteSpace(port))
                    result.Add((name, port.Trim()));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading SERIALCOMM failed: {ex.Message}");
        }
        return result;
    }

    private static unsafe void QuerySetupApi(Guid classGuid, Dictionary<string, (string Desc, string Instance)> into)
    {
        IntPtr set = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE) return;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            char* idBuf = stackalloc char[512];
            byte* propBuf = stackalloc byte[1024];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                string? port = null;
                IntPtr hkey = SetupDiOpenDevRegKey(set, ref data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
                if (hkey != INVALID_HANDLE_VALUE && hkey != IntPtr.Zero)
                {
                    using var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, ownsHandle: true));
                    port = (key.GetValue("PortName") as string)?.Trim();
                }
                if (string.IsNullOrEmpty(port) || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) continue;

                string instance = SetupDiGetDeviceInstanceIdW(set, ref data, idBuf, 512, out _) ? new string(idBuf) : "";
                string? desc = GetStringProperty(set, ref data, SPDRP_FRIENDLYNAME, propBuf, 1024)
                               ?? GetStringProperty(set, ref data, SPDRP_DEVICEDESC, propBuf, 1024);
                into[port] = (CleanDescription(desc, port), instance);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static unsafe string? GetStringProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, byte* buf, uint size)
    {
        new Span<byte>(buf, (int)size).Clear();
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, buf, size - 2, out _)) return null;
        var s = new string((char*)buf);
        return s.Length == 0 ? null : s;
    }

    /// <summary>Fallback for ports SetupAPI did not report: scan Enum\{bus}\{device}\{instance}\Device Parameters\PortName.</summary>
    private static void QueryEnumRegistry(Dictionary<string, (string Desc, string Instance)> into, HashSet<string> wanted)
    {
        if (wanted.Count == 0) return;
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (enumKey is null) return;
        foreach (var bus in new[] { "USB", "FTDIBUS", "BTHENUM", "ACPI", "PCI", "ROOT" })
        {
            using var busKey = enumKey.OpenSubKey(bus);
            if (busKey is null) continue;
            foreach (var devName in busKey.GetSubKeyNames())
            {
                using var devKey = busKey.OpenSubKey(devName);
                if (devKey is null) continue;
                foreach (var instName in devKey.GetSubKeyNames())
                {
                    using var instKey = devKey.OpenSubKey(instName);
                    using var parms = instKey?.OpenSubKey("Device Parameters");
                    if (parms?.GetValue("PortName") is not string port) continue;
                    port = port.Trim();
                    if (!wanted.Contains(port) || into.ContainsKey(port)) continue;
                    string? desc = instKey!.GetValue("FriendlyName") as string ?? instKey.GetValue("DeviceDesc") as string;
                    into[port] = (CleanDescription(desc, port), $@"{bus}\{devName}\{instName}");
                }
            }
        }
    }

    // ---- SetupAPI ----
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x2;
    private const uint DICS_FLAG_GLOBAL = 1, DIREG_DEV = 1;
    private const int KEY_READ = 0x20019;
    private const uint SPDRP_DEVICEDESC = 0x0, SPDRP_FRIENDLYNAME = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char* buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property,
        out uint regType, byte* buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA data, uint scope, uint hwProfile, uint keyType, int samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
