using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RambiBox.Core.Devices;

internal sealed class Win32SerialLinkFactory : ISerialLinkFactory
{
    public ISerialLink Open(string portName, int baudRate) => Win32SerialLink.Open(portName, baudRate);
}

/// <summary>
/// Minimal synchronous Win32 serial port. Deliberately not System.IO.Ports.SerialPort: no background
/// event thread, no DataReceived, no finalizer that can throw after surprise removal — every failure
/// surfaces as an exception on the calling (device) thread, and closing never throws.
/// </summary>
internal sealed unsafe class Win32SerialLink : ISerialLink
{
    private readonly SafeFileHandle _h;
    private uint _readTimeoutMs = uint.MaxValue;
    private readonly uint _writeMultiplier;
    private int _disposed;

    private Win32SerialLink(SafeFileHandle h, string portName, uint writeMultiplier)
    {
        _h = h;
        PortName = portName;
        _writeMultiplier = writeMultiplier;
    }

    public string PortName { get; }

    public static Win32SerialLink Open(string portName, int baudRate)
    {
        var h = CreateFileW(@"\\.\" + portName, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw MakeException(err, portName, "open");
        }
        try
        {
            if (GetFileType(h) != FILE_TYPE_CHAR)
                throw new IOException($"{portName} is not a serial port.");

            SetupComm(h, 4096, 4096); // best effort

            DCB dcb = default;
            dcb.DCBlength = (uint)sizeof(DCB);
            if (!GetCommState(h, ref dcb)) throw MakeException(Marshal.GetLastPInvokeError(), portName, "GetCommState");
            dcb.BaudRate = (uint)baudRate;
            dcb.ByteSize = 8;
            dcb.Parity = 0;   // NOPARITY
            dcb.StopBits = 0; // ONESTOPBIT
            // Clear all flow-control / error-handling bits, then: binary mode, DTR on, RTS on.
            // DTR asserted: required by native-USB boards (Leonardo/Pro Micro) to transmit; on CH340 Nanos the
            // board resets on open either way. DTR+RTS both asserted keeps ESP auto-reset circuits in run mode.
            dcb.Flags = DCB_fBinary | (DTR_CONTROL_ENABLE << 4) | (RTS_CONTROL_ENABLE << 12);
            if (!SetCommState(h, ref dcb)) throw MakeException(Marshal.GetLastPInvokeError(), portName, $"set {baudRate} baud");

            uint writeMultiplier = (uint)Math.Max(1, (int)Math.Ceiling(20000.0 / Math.Max(1, baudRate))); // ms per byte, generous
            var to = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = MAXDWORD,
                ReadTotalTimeoutMultiplier = MAXDWORD,
                ReadTotalTimeoutConstant = 50,
                WriteTotalTimeoutMultiplier = writeMultiplier,
                WriteTotalTimeoutConstant = 300,
            };
            if (!SetCommTimeouts(h, ref to)) throw MakeException(Marshal.GetLastPInvokeError(), portName, "SetCommTimeouts");
            PurgeComm(h, PURGE_RXCLEAR | PURGE_TXCLEAR | PURGE_RXABORT | PURGE_TXABORT);
            ClearCommError(h, out _, IntPtr.Zero);
            var link = new Win32SerialLink(h, portName, writeMultiplier) { _readTimeoutMs = 50 };
            return link;
        }
        catch
        {
            try { h.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        fixed (byte* p = data)
        {
            if (!WriteFile(_h, p, (uint)data.Length, out uint written, IntPtr.Zero))
                throw MakeException(Marshal.GetLastPInvokeError(), PortName, "write");
            if (written != (uint)data.Length)
                throw new TimeoutException($"{PortName}: write timed out ({written}/{data.Length} bytes).");
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        uint t = (uint)Math.Max(1, timeoutMs);
        if (t != _readTimeoutMs)
        {
            var to = new COMMTIMEOUTS
            {
                ReadIntervalTimeout = MAXDWORD,
                ReadTotalTimeoutMultiplier = MAXDWORD,
                ReadTotalTimeoutConstant = t,
                WriteTotalTimeoutMultiplier = _writeMultiplier,
                WriteTotalTimeoutConstant = 300,
            };
            if (!SetCommTimeouts(_h, ref to)) throw MakeException(Marshal.GetLastPInvokeError(), PortName, "SetCommTimeouts");
            _readTimeoutMs = t;
        }
        fixed (byte* p = buffer)
        {
            if (!ReadFile(_h, p, (uint)buffer.Length, out uint read, IntPtr.Zero))
                throw MakeException(Marshal.GetLastPInvokeError(), PortName, "read");
            return (int)read;
        }
    }

    public int BytesToWrite
    {
        get
        {
            if (_disposed != 0) return -1;
            COMSTAT st;
            return ClearCommError(_h, out _, (IntPtr)(&st)) ? (int)st.cbOutQue : -1;
        }
    }

    public void DiscardInput()
    {
        if (_disposed == 0) PurgeComm(_h, PURGE_RXCLEAR);
    }

    public void CancelPendingIo()
    {
        if (_disposed != 0) return;
        try { CancelIoEx(_h, IntPtr.Zero); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // After surprise removal CloseHandle may fail; never let that escape.
        try { _h.Dispose(); } catch { /* ignore */ }
    }

    private static Exception MakeException(int err, string port, string what)
    {
        string msg = $"{port}: {what} failed: {new Win32Exception(err).Message} (error {err})";
        return err switch
        {
            ERROR_ACCESS_DENIED => new UnauthorizedAccessException($"{port} is in use by another program (access denied)."),
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => new FileNotFoundException(msg, port),
            ERROR_OPERATION_ABORTED => new TimeoutException(msg),
            _ => new IOException(msg, HResultFromWin32(err)),
        };
    }

    private static int HResultFromWin32(int err) => err <= 0 ? err : (int)((uint)err & 0xFFFF | 0x80070000);

    // ---- Win32 ----
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x80, FILE_TYPE_CHAR = 2;
    private const uint MAXDWORD = 0xFFFFFFFF;
    private const uint PURGE_TXABORT = 1, PURGE_RXABORT = 2, PURGE_TXCLEAR = 4, PURGE_RXCLEAR = 8;
    private const uint DCB_fBinary = 1, DTR_CONTROL_ENABLE = 1, RTS_CONTROL_ENABLE = 1;
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5, ERROR_OPERATION_ABORTED = 995;

    [StructLayout(LayoutKind.Sequential)]
    private struct DCB
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public sbyte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMSTAT
    {
        public uint Flags;
        public uint cbInQue;
        public uint cbOutQue;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle h);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupComm(SafeFileHandle h, uint inQueue, uint outQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PurgeComm(SafeFileHandle h, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClearCommError(SafeFileHandle h, out uint errors, IntPtr stat);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle h, byte* buffer, uint count, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle h, byte* buffer, uint count, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
