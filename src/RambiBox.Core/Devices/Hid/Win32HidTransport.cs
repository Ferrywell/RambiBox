using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RambiBox.Core.Devices.Hid;

/// <summary>
/// HID access through the plain Windows API (setupapi + hid.dll + kernel32), no external libraries:
/// SetupDiGetClassDevs(HidD_GetHidGuid) → interface paths → CreateFile → HidD_GetAttributes / serial / HidP caps.
/// <para>
/// Enumeration is read-only and conservative: the VID/PID is parsed from the interface path first, so devices that
/// clearly don't match are never opened at all; otherwise the device is opened with zero access rights (query only,
/// like hidapi's enumerate), which works even for keyboards/mice held exclusively by Windows.
/// </para>
/// </summary>
internal sealed unsafe class Win32HidTransport : IHidTransport
{
    public static Win32HidTransport Shared { get; } = new();

    public IReadOnlyList<HidDeviceInfo> Enumerate(Func<ushort, ushort, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var result = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs failed");
        try
        {
            var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
            for (uint i = 0; i < 4096 && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd); i++)
            {
                string? path = GetInterfacePath(set, ref ifd);
                if (path is null) continue;
                if (TryParseVidPid(path, out ushort pv, out ushort pp) && !match(pv, pp)) continue;
                var info = Query(path, match);
                if (info is not null) result.Add(info);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    public IHidConnection Open(HidDeviceInfo device, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        uint access = readOnly ? GENERIC_READ : GENERIC_READ | GENERIC_WRITE;
        var h = CreateFileW(device.Path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new HidException($"cannot open HID device {device.UsbId}: {new Win32Exception(err).Message}", err);
        }
        try
        {
            return new Win32HidConnection(h, device);
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>Extracts "vid_XXXX" and "pid_XXXX" from a USB HID interface path. False for paths without them (Bluetooth LE, virtual…).</summary>
    internal static bool TryParseVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        int v = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
        int p = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (v < 0 || p < 0 || v + 8 > path.Length || p + 8 > path.Length) return false;
        return ushort.TryParse(path.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(path.AsSpan(p + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static string? GetInterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifd)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref ifd, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required < 8 || required > 64 * 1024) return null;
        IntPtr buf = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on 64-bit (DWORD + WCHAR[1] + padding), 6 on 32-bit.
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifd, buf, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static HidDeviceInfo? Query(string path, Func<ushort, ushort, bool> match)
    {
        // Zero access: attributes, strings and caps only; nothing can be written through this handle.
        using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var attr = new HIDD_ATTRIBUTES { Size = (uint)sizeof(HIDD_ATTRIBUTES) };
        if (!HidD_GetAttributes(h, ref attr)) return null;
        if (!match(attr.VendorID, attr.ProductID)) return null;

        string? serial = GetString(h, serial: true);
        string? product = GetString(h, serial: false);
        int inLen = 0, outLen = 0;
        ushort usagePage = 0, usage = 0;
        if (HidD_GetPreparsedData(h, out IntPtr pp))
        {
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS)
                {
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                }
            }
            finally
            {
                HidD_FreePreparsedData(pp);
            }
        }
        return new HidDeviceInfo(path, attr.VendorID, attr.ProductID, attr.VersionNumber, serial, product, inLen, outLen, usagePage, usage);
    }

    private static string? GetString(SafeFileHandle h, bool serial)
    {
        // USB string descriptors hold at most 126 UTF-16 characters.
        char* buf = stackalloc char[128];
        new Span<char>(buf, 128).Clear();
        bool ok = serial
            ? HidD_GetSerialNumberString(h, buf, 127 * sizeof(char))
            : HidD_GetProductString(h, buf, 127 * sizeof(char));
        if (!ok) return null;
        var s = new string(buf).Trim();
        return s.Length == 0 ? null : s;
    }

    // =====================================================================================
    // Native
    // =====================================================================================

    internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public fixed ushort Counts[10];
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle h, char* buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle h, char* buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}

/// <summary>
/// An open HID device. Writes use overlapped WriteFile with a timeout (cancelled with CancelIoEx), falling back to
/// HidD_SetOutputReport when the driver rejects WriteFile. The report is copied into a native buffer, so a write
/// allocates nothing.
/// </summary>
internal sealed unsafe class Win32HidConnection : IHidConnection
{
    private const int ERROR_INVALID_FUNCTION = 1, ERROR_NOT_SUPPORTED = 50, ERROR_IO_PENDING = 997, ERROR_TIMEOUT = 1460;
    private const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;

    private readonly SafeFileHandle _h;
    private readonly IntPtr _event;
    private readonly NativeOverlapped* _ov;
    private readonly byte* _buf;
    private readonly int _bufLen;
    private bool _useSetOutputReport;
    private int _disposed;

    public HidDeviceInfo Device { get; }

    internal Win32HidConnection(SafeFileHandle h, HidDeviceInfo device)
    {
        _h = h;
        Device = device;
        _bufLen = Math.Max(device.OutputReportLength, 65);
        _event = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (_event == IntPtr.Zero) throw new HidException("CreateEvent failed", Marshal.GetLastPInvokeError());
        _ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _buf = (byte*)NativeMemory.AllocZeroed((nuint)_bufLen);
    }

    public bool Write(ReadOnlySpan<byte> report, int timeoutMs, out int error)
    {
        error = 0;
        if (Volatile.Read(ref _disposed) != 0) { error = 6; return false; } // ERROR_INVALID_HANDLE
        // Windows requires exactly OutputReportByteLength bytes.
        int len = Device.OutputReportLength > 0 ? Device.OutputReportLength : Math.Min(report.Length, _bufLen);
        var dst = new Span<byte>(_buf, _bufLen);
        int n = Math.Min(report.Length, len);
        report[..n].CopyTo(dst);
        dst[n..len].Clear();
        try
        {
            if (_useSetOutputReport) return SetOutputReport(len, out error);

            ResetEvent(_event);
            *_ov = default;
            _ov->EventHandle = _event;
            if (!WriteFile(_h, _buf, (uint)len, null, _ov))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ERROR_INVALID_FUNCTION || err == ERROR_NOT_SUPPORTED)
                {
                    _useSetOutputReport = true; // HID minidriver without WriteFile support: use the control pipe
                    return SetOutputReport(len, out error);
                }
                if (err != ERROR_IO_PENDING)
                {
                    error = err;
                    return false;
                }
                uint w = WaitForSingleObject(_event, (uint)Math.Max(1, timeoutMs));
                if (w != WAIT_OBJECT_0)
                {
                    CancelIoEx(_h, _ov);
                    GetOverlappedResult(_h, _ov, out _, true); // the OVERLAPPED must stay valid until the cancel completed
                    error = w == WAIT_TIMEOUT ? ERROR_TIMEOUT : Marshal.GetLastPInvokeError();
                    return false;
                }
            }
            if (!GetOverlappedResult(_h, _ov, out uint written, false))
            {
                error = Marshal.GetLastPInvokeError();
                return false;
            }
            if (written == 0)
            {
                error = 31; // ERROR_GEN_FAILURE
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            error = 6;
            return false;
        }
    }

    private bool SetOutputReport(int len, out int error)
    {
        error = 0;
        if (HidD_SetOutputReport(_h, _buf, (uint)len)) return true;
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    public bool TryGetInputReport(Span<byte> buffer)
    {
        if (Volatile.Read(ref _disposed) != 0 || buffer.Length == 0) return false;
        int len = Device.InputReportLength > 0 ? Math.Min(Device.InputReportLength, buffer.Length) : buffer.Length;
        buffer.Clear(); // byte 0 = report id 0
        try
        {
            fixed (byte* p = buffer) return HidD_GetInputReport(_h, p, (uint)len);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    public void CancelIo()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { CancelIoEx(_h, null); } catch { /* closed meanwhile */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { CancelIoEx(_h, null); } catch { /* ignore */ }
        _h.Dispose();
        CloseHandle(_event);
        NativeMemory.Free(_ov);
        NativeMemory.Free(_buf);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle h, byte* buffer, uint count, uint* written, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle h, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr security, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ResetEvent(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte* report, uint length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetInputReport(SafeFileHandle h, byte* report, uint length);
}
