using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace RambiBox.Core.Capture;

/// <summary>
/// Hand-written COM / WinRT ABI calls for Windows.Graphics.Capture.
/// <para>Two reasons: (1) the classic-COM interop interfaces (IGraphicsCaptureItemInterop, IDirect3DDxgiInterfaceAccess,
/// CreateDirect3D11DeviceFromDXGIDevice) have no projection; (2) the per-frame calls (TryGetNextFrame, Surface,
/// ContentSize, Close) go straight through the v-table so steady-state capture creates no managed wrapper objects —
/// the CsWinRT projection would allocate an RCW per frame and per surface. (3) Newer session properties
/// (IsBorderRequired: build 20348, MinUpdateInterval: 26100) and GraphicsCaptureAccess are not in the 19041
/// projection this app targets, so they are called through their interfaces (IIDs from the OS metadata, Windows.Graphics.winmd).</para>
/// </summary>
internal static unsafe class WgcInterop
{
    // ---- Classic COM interop interfaces
    public static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    // ---- WinRT interfaces (Windows.Graphics.winmd / Windows.Foundation.winmd)
    public static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    public static readonly Guid IID_IDirect3D11CaptureFramePool = new("24eb6d22-1975-422e-82e7-780dbd8ddf24");
    public static readonly Guid IID_IDirect3D11CaptureFrame = new("fa50c623-38da-4b32-acf3-fa9734ad800e");
    public static readonly Guid IID_IGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be"); // IsBorderRequired
    public static readonly Guid IID_IGraphicsCaptureSession5 = new("67c0ea62-1f85-5061-925a-239be0ac09cb"); // MinUpdateInterval
    public static readonly Guid IID_IGraphicsCaptureAccessStatics = new("743ed370-06ec-5040-a58a-901f0f757095");
    public static readonly Guid IID_IClosable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    public static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    public const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    public const string GraphicsCaptureAccessClass = "Windows.Graphics.Capture.GraphicsCaptureAccess";

    // WinRT methods start after IUnknown (3) + IInspectable (3) slots.
    private const int FirstWinRtSlot = 6;

    private static void** Vtbl(IntPtr p) => *(void***)p;

    // =====================================================================================================
    // Setup (once per session)
    // =====================================================================================================

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>RoGetActivationFactory for <paramref name="className"/>, asking for <paramref name="iid"/>.</summary>
    public static int GetActivationFactory(string className, in Guid iid, out IntPtr factory)
    {
        factory = IntPtr.Zero;
        int hr = WindowsCreateString(className, className.Length, out var hs);
        if (hr < 0) return hr;
        try { return RoGetActivationFactory(hs, iid, out factory); }
        finally { WindowsDeleteString(hs); }
    }

    /// <summary>Wraps a D3D11 device (its ID3D11Device* or any interface of it) as a WinRT IDirect3DDevice.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(IntPtr d3dDevice)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in IID_IDXGIDevice, out var dxgiDevice));
        IntPtr inspectable = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(dxgiDevice);
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
        }
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForMonitor (v-table slot 4).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
    {
        Marshal.ThrowExceptionForHR(GetActivationFactory(GraphicsCaptureItemClass, IID_IGraphicsCaptureItemInterop, out var interop));
        IntPtr item = IntPtr.Zero;
        try
        {
            Guid iid = IID_IGraphicsCaptureItem;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(interop)[4])(interop, hmonitor, &iid, &item);
            Marshal.ThrowExceptionForHR(hr);
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            Marshal.Release(interop);
            if (item != IntPtr.Zero) Marshal.Release(item);
        }
    }

    /// <summary>The raw ABI pointer of a projected WinRT object for <paramref name="iid"/> (AddRef'ed; null if not supported).</summary>
    public static IntPtr QueryInterface(object projected, in Guid iid)
    {
        var thisPtr = ((WinRT.IWinRTObject)projected).NativeObject.ThisPtr;
        return Marshal.QueryInterface(thisPtr, in iid, out var p) >= 0 ? p : IntPtr.Zero;
    }

    /// <summary>IGraphicsCaptureSession3::put_IsBorderRequired (slot 7). Returns the HRESULT (E_NOINTERFACE before build 20348).</summary>
    public static int SetIsBorderRequired(GraphicsCaptureSession session, bool required)
    {
        var p = QueryInterface(session, IID_IGraphicsCaptureSession3);
        if (p == IntPtr.Zero) return unchecked((int)0x80004002);
        try { return ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Vtbl(p)[FirstWinRtSlot + 1])(p, required ? (byte)1 : (byte)0); }
        finally { Marshal.Release(p); }
    }

    /// <summary>IGraphicsCaptureSession5::put_MinUpdateInterval (slot 7, TimeSpan = 100 ns ticks). E_NOINTERFACE before 24H2.</summary>
    public static int SetMinUpdateInterval(GraphicsCaptureSession session, TimeSpan interval)
    {
        var p = QueryInterface(session, IID_IGraphicsCaptureSession5);
        if (p == IntPtr.Zero) return unchecked((int)0x80004002);
        try { return ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Vtbl(p)[FirstWinRtSlot + 1])(p, interval.Ticks); }
        finally { Marshal.Release(p); }
    }

    /// <summary>
    /// GraphicsCaptureAccess.RequestAccessAsync(kind) (build 20348+), waited for synchronously with a timeout by polling
    /// IAsyncInfo (no completion delegate needed). Returns the AppCapabilityAccessStatus (4 = Allowed) or null when
    /// unsupported / timed out / failed (<paramref name="hr"/> says why).
    /// </summary>
    public static int? RequestAccess(int kind, int timeoutMs, out int hr)
    {
        hr = GetActivationFactory(GraphicsCaptureAccessClass, IID_IGraphicsCaptureAccessStatics, out var statics);
        if (hr < 0) return null;
        IntPtr op = IntPtr.Zero, info = IntPtr.Zero;
        try
        {
            IntPtr o;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)Vtbl(statics)[FirstWinRtSlot])(statics, kind, &o);
            if (hr < 0) return null;
            op = o;
            hr = Marshal.QueryInterface(op, in IID_IAsyncInfo, out info);
            if (hr < 0) return null;
            var sw = Stopwatch.StartNew();
            int status;
            while (true)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(info)[FirstWinRtSlot + 1])(info, &status); // get_Status
                if (hr < 0) return null;
                if (status != 0 /* Started */) break;
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(info)[FirstWinRtSlot + 3])(info); // Cancel
                    hr = unchecked((int)0x800705B4); // ERROR_TIMEOUT
                    return null;
                }
                Thread.Sleep(5);
            }
            if (status != 1 /* Completed */)
            {
                int err;
                ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(info)[FirstWinRtSlot + 2])(info, &err); // get_ErrorCode
                hr = err < 0 ? err : unchecked((int)0x80004004); // E_ABORT (cancelled)
                return null;
            }
            int result;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(op)[FirstWinRtSlot + 2])(op, &result); // IAsyncOperation<T>::GetResults
            return hr < 0 ? null : result;
        }
        finally
        {
            if (info != IntPtr.Zero)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(info)[FirstWinRtSlot + 4])(info); // Close
                Marshal.Release(info);
            }
            if (op != IntPtr.Zero) Marshal.Release(op);
            Marshal.Release(statics);
        }
    }

    // =====================================================================================================
    // Per frame (allocation free)
    // =====================================================================================================

    /// <summary>IDirect3D11CaptureFramePool::TryGetNextFrame (slot 7). <paramref name="frame"/> is zero when none is queued.</summary>
    public static int TryGetNextFrame(IntPtr pool, out IntPtr frame)
    {
        IntPtr f = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(pool)[FirstWinRtSlot + 1])(pool, &f);
        frame = f;
        return hr;
    }

    /// <summary>IDirect3D11CaptureFrame::get_Surface (slot 6) → IDirect3DSurface*.</summary>
    public static int GetSurface(IntPtr frame, out IntPtr surface)
    {
        IntPtr s = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(frame)[FirstWinRtSlot])(frame, &s);
        surface = s;
        return hr;
    }

    /// <summary>IDirect3D11CaptureFrame::get_SystemRelativeTime (slot 7), 100 ns QPC-based ticks.</summary>
    public static int GetSystemRelativeTime(IntPtr frame, out long ticks)
    {
        long t;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Vtbl(frame)[FirstWinRtSlot + 1])(frame, &t);
        ticks = t;
        return hr;
    }

    /// <summary>IDirect3D11CaptureFrame::get_ContentSize (slot 8).</summary>
    public static int GetContentSize(IntPtr frame, out SizeInt32 size)
    {
        SizeInt32 s;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)Vtbl(frame)[FirstWinRtSlot + 2])(frame, &s);
        size = s;
        return hr;
    }

    /// <summary>IClosable::Close on an object (QI'd first). Returns the frame buffer to the pool right away.</summary>
    public static void Close(IntPtr obj)
    {
        if (Marshal.QueryInterface(obj, in IID_IClosable, out var c) < 0) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(c)[FirstWinRtSlot])(c);
        Marshal.Release(c);
    }

    /// <summary>IDirect3DSurface* → ID3D11Texture2D* via IDirect3DDxgiInterfaceAccess::GetInterface (slot 3).</summary>
    public static int GetTexture(IntPtr surface, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        int hr = Marshal.QueryInterface(surface, in IID_IDirect3DDxgiInterfaceAccess, out var access);
        if (hr < 0) return hr;
        try
        {
            Guid iid = NativeCom.IID_ID3D11Texture2D;
            IntPtr t = IntPtr.Zero;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(access)[3])(access, &iid, &t);
            texture = t;
            return hr;
        }
        finally
        {
            Marshal.Release(access);
        }
    }
}

/// <summary>Secure desktop (lock screen, UAC prompt, Ctrl+Alt+Del) detection: WGC frames simply stop there.</summary>
internal static unsafe class InputDesktop
{
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, int inherit, uint desiredAccess);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetUserObjectInformationW(IntPtr obj, int index, char* info, int length, out int needed);

    /// <summary>
    /// True when the interactive desktop is the normal "Default" desktop. The secure desktop (Winlogon) can't be
    /// opened by user processes, so OpenInputDesktop fails there. Allocation free.
    /// </summary>
    public static bool IsDefaultDesktopActive()
    {
        const uint DESKTOP_READOBJECTS = 0x0001;
        const int UOI_NAME = 2;
        var h = OpenInputDesktop(0, 0, DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return false;
        try
        {
            char* name = stackalloc char[64];
            if (GetUserObjectInformationW(h, UOI_NAME, name, 64 * sizeof(char), out _) == 0) return true; // unknown → assume normal
            return new ReadOnlySpan<char>(name, 64).StartsWith("Default\0", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(h);
        }
    }
}
