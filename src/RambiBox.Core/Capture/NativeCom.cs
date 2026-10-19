using System.Runtime.InteropServices;

namespace RambiBox.Core.Capture;

/// <summary>
/// Raw COM v-table calls for the per-frame hot path. Vortice wraps every returned interface in a new managed object
/// (AcquireNextFrame → new IDXGIResource, QueryInterface → new ID3D11Texture2D), which would allocate on every
/// frame; calling through the v-table keeps steady-state capture allocation free.
/// </summary>
internal static unsafe class NativeCom
{
    // HRESULTs
    public const int S_OK = 0;
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);
    public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
    public const int DXGI_ERROR_DRIVER_INTERNAL_ERROR = unchecked((int)0x887A0020);
    public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    public const int DXGI_ERROR_MODE_CHANGE_IN_PROGRESS = unchecked((int)0x887A0025);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    public const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    public const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [StructLayout(LayoutKind.Sequential)]
    public struct OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    private static void** Vtbl(IntPtr p) => *(void***)p;

    /// <summary>IDXGIOutputDuplication::AcquireNextFrame (v-table slot 8).</summary>
    public static int AcquireNextFrame(IntPtr dupl, uint timeoutMs, out OUTDUPL_FRAME_INFO info, out IntPtr resource)
    {
        OUTDUPL_FRAME_INFO fi;
        IntPtr res = IntPtr.Zero;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, OUTDUPL_FRAME_INFO*, IntPtr*, int>)Vtbl(dupl)[8])(dupl, timeoutMs, &fi, &res);
        info = fi;
        resource = res;
        return hr;
    }

    /// <summary>IDXGIOutputDuplication::ReleaseFrame (v-table slot 14).</summary>
    public static int ReleaseFrame(IntPtr dupl)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(dupl)[14])(dupl);

    /// <summary>ID3D11Texture2D::GetDesc (v-table slot 10).</summary>
    public static void GetTextureDesc(IntPtr tex, out TEXTURE2D_DESC desc)
    {
        TEXTURE2D_DESC d;
        ((delegate* unmanaged[Stdcall]<IntPtr, TEXTURE2D_DESC*, void>)Vtbl(tex)[10])(tex, &d);
        desc = d;
    }

    /// <summary>ID3D11DeviceContext::CopySubresourceRegion (v-table slot 46), whole subresource.</summary>
    public static void CopySubresourceRegion(IntPtr context, IntPtr dst, uint dstSubresource, IntPtr src, uint srcSubresource)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)Vtbl(context)[46])(
            context, dst, dstSubresource, 0, 0, 0, src, srcSubresource, null);

    public static int QueryInterface(IntPtr unk, in Guid iid, out IntPtr result) => Marshal.QueryInterface(unk, in iid, out result);

    public static void Release(ref IntPtr p)
    {
        if (p != IntPtr.Zero)
        {
            Marshal.Release(p);
            p = IntPtr.Zero;
        }
    }

    public static string Describe(int hr) => hr switch
    {
        DXGI_ERROR_ACCESS_LOST => "DXGI_ERROR_ACCESS_LOST",
        DXGI_ERROR_WAIT_TIMEOUT => "DXGI_ERROR_WAIT_TIMEOUT",
        DXGI_ERROR_DEVICE_REMOVED => "DXGI_ERROR_DEVICE_REMOVED",
        DXGI_ERROR_DEVICE_RESET => "DXGI_ERROR_DEVICE_RESET",
        DXGI_ERROR_DEVICE_HUNG => "DXGI_ERROR_DEVICE_HUNG",
        DXGI_ERROR_INVALID_CALL => "DXGI_ERROR_INVALID_CALL",
        DXGI_ERROR_UNSUPPORTED => "DXGI_ERROR_UNSUPPORTED",
        DXGI_ERROR_NOT_CURRENTLY_AVAILABLE => "DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        DXGI_ERROR_SESSION_DISCONNECTED => "DXGI_ERROR_SESSION_DISCONNECTED",
        DXGI_ERROR_MODE_CHANGE_IN_PROGRESS => "DXGI_ERROR_MODE_CHANGE_IN_PROGRESS",
        DXGI_ERROR_ACCESS_DENIED => "DXGI_ERROR_ACCESS_DENIED",
        DXGI_ERROR_NOT_FOUND => "DXGI_ERROR_NOT_FOUND",
        E_ACCESSDENIED => "E_ACCESSDENIED",
        E_INVALIDARG => "E_INVALIDARG",
        E_OUTOFMEMORY => "E_OUTOFMEMORY",
        _ => $"0x{hr:X8}",
    };
}
