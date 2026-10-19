using System.Runtime.InteropServices;

namespace RambiBox.Core.Devices.AlienFx;

/// <summary>LFX_COLOR (LFXDecl.h): red, green, blue, brightness — 4 unsigned chars.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct LfxColor(byte Red, byte Green, byte Blue, byte Brightness)
{
    public static LfxColor Black => new(0, 0, 0, 0xFF);
}

/// <summary>LFX_RESULT codes (LFXDecl.h).</summary>
internal static class LfxResult
{
    public const uint Success = 0, Failure = 1, NoInit = 2, NoDevs = 3, NoLights = 4, BuffSize = 5;

    public static string Name(uint r) => r switch
    {
        Success => "LFX_SUCCESS",
        Failure => "LFX_FAILURE",
        NoInit => "LFX_ERROR_NOINIT",
        NoDevs => "LFX_ERROR_NODEVS",
        NoLights => "LFX_ERROR_NOLIGHTS",
        BuffSize => "LFX_ERROR_BUFFSIZE",
        _ => $"LFX result {r}",
    };
}

/// <summary>
/// The subset of the Alienware LightFX 2.0 API (LFX2.h) that RambiBox uses, as Prismatik's LedDeviceAlienFx does.
/// Abstracted so the driver logic is testable without Alienware hardware.
/// </summary>
internal interface ILightFx
{
    /// <summary>LFX_Initialize: Success, NoDevs (initialized, no devices) or Failure. Pair every Success/NoDevs with <see cref="Release"/>.</summary>
    uint Initialize();
    /// <summary>LFX_Release.</summary>
    uint Release();
    /// <summary>LFX_Reset: all lights off (takes effect on <see cref="Update"/>).</summary>
    uint Reset();
    /// <summary>LFX_Update: submits the state to the hardware.</summary>
    uint Update();
    uint GetNumDevices(out uint count);
    uint GetDeviceDescription(uint device, out string description, out byte deviceType);
    uint GetNumLights(uint device, out uint count);
    uint SetLightColor(uint device, uint light, in LfxColor color);
}

/// <summary>Supplies the LightFX API when it is installed.</summary>
internal interface ILightFxProvider
{
    /// <summary>The API, or null with a reason when LightFX.dll is not available.</summary>
    ILightFx? TryGet(out string? reason);
}

/// <summary>
/// Loads LightFX.dll (shipped with Alienware Command Center / the AlienFX SDK) dynamically, only from the system
/// directory and Alienware install folders (never from the app folder or the current directory). The DLL keeps
/// process-wide state, so this is one shared instance: calls are serialized and <see cref="Initialize"/>/<see cref="Release"/>
/// are reference counted, letting discovery run while the driver is active.
/// </summary>
internal sealed unsafe class NativeLightFx : ILightFx, ILightFxProvider
{
    private const string DllName = "LightFX.dll";

    public static NativeLightFx Shared { get; } = new();

    private readonly object _gate = new();
    private bool _loadAttempted;
    private string? _loadError;
    private IntPtr _lib;
    private int _initCount;

    private delegate* unmanaged<uint> _initialize, _release, _reset, _update;
    private delegate* unmanaged<uint*, uint> _getNumDevices;
    private delegate* unmanaged<uint, byte*, uint, byte*, uint> _getDeviceDescription;
    private delegate* unmanaged<uint, uint*, uint> _getNumLights;
    private delegate* unmanaged<uint, uint, LfxColor*, uint> _setLightColor;

    private NativeLightFx() { }

    /// <summary>Places LightFX.dll is looked for (64-bit builds only; the x86 copies under Program Files (x86) can't load).</summary>
    internal static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(Environment.SystemDirectory, DllName);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(pf)) yield break;
        yield return Path.Combine(pf, "Alienware", "Command Center", DllName);
        yield return Path.Combine(pf, "Alienware", "Alienware Command Center", DllName);
        yield return Path.Combine(pf, "Alienware", "Command Center", "AlienFX SDK", "DLLs", "x64", DllName);
        yield return Path.Combine(pf, "Alienware", "AlienFX", DllName);
    }

    public ILightFx? TryGet(out string? reason)
    {
        lock (_gate)
        {
            if (!_loadAttempted)
            {
                _loadAttempted = true;
                _loadError = Load();
            }
            reason = _loadError;
            return _lib != IntPtr.Zero ? this : null;
        }
    }

    private string? Load()
    {
        var tried = new List<string>();
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                tried.Add(path);
                if (!NativeLibrary.TryLoad(path, out IntPtr lib)) continue;
                if (Bind(lib))
                {
                    _lib = lib;
                    Log.Info($"AlienFX: loaded {path}");
                    return null;
                }
                NativeLibrary.Free(lib);
            }
            catch (Exception ex)
            {
                Log.Warn($"AlienFX: loading {path} failed: {ex.Message}");
            }
        }
        return tried.Count == 0
            ? "LightFX.dll not found"
            : $"LightFX.dll could not be loaded or lacks the LightFX 2 exports ({string.Join(", ", tried)})";
    }

    private bool Bind(IntPtr lib)
    {
        IntPtr Get(string name) => NativeLibrary.TryGetExport(lib, name, out IntPtr p) ? p : IntPtr.Zero;
        IntPtr init = Get("LFX_Initialize"), rel = Get("LFX_Release"), reset = Get("LFX_Reset"), upd = Get("LFX_Update"),
            nd = Get("LFX_GetNumDevices"), dd = Get("LFX_GetDeviceDescription"), nl = Get("LFX_GetNumLights"), slc = Get("LFX_SetLightColor");
        if (init == 0 || rel == 0 || reset == 0 || upd == 0 || nd == 0 || nl == 0 || slc == 0) return false;
        _initialize = (delegate* unmanaged<uint>)init;
        _release = (delegate* unmanaged<uint>)rel;
        _reset = (delegate* unmanaged<uint>)reset;
        _update = (delegate* unmanaged<uint>)upd;
        _getNumDevices = (delegate* unmanaged<uint*, uint>)nd;
        _getDeviceDescription = (delegate* unmanaged<uint, byte*, uint, byte*, uint>)dd; // optional
        _getNumLights = (delegate* unmanaged<uint, uint*, uint>)nl;
        _setLightColor = (delegate* unmanaged<uint, uint, LfxColor*, uint>)slc;
        return true;
    }

    public uint Initialize()
    {
        lock (_gate)
        {
            if (_lib == IntPtr.Zero) return LfxResult.Failure;
            uint r = _initialize();
            if (r is LfxResult.Success or LfxResult.NoDevs) _initCount++;
            return r;
        }
    }

    public uint Release()
    {
        lock (_gate)
        {
            if (_lib == IntPtr.Zero || _initCount == 0) return LfxResult.Success;
            return --_initCount == 0 ? _release() : LfxResult.Success;
        }
    }

    public uint Reset()
    {
        lock (_gate) return _initCount == 0 ? LfxResult.NoInit : _reset();
    }

    public uint Update()
    {
        lock (_gate) return _initCount == 0 ? LfxResult.NoInit : _update();
    }

    public uint GetNumDevices(out uint count)
    {
        count = 0;
        lock (_gate)
        {
            if (_initCount == 0) return LfxResult.NoInit;
            uint c = 0;
            uint r = _getNumDevices(&c);
            count = c;
            return r;
        }
    }

    public uint GetDeviceDescription(uint device, out string description, out byte deviceType)
    {
        description = "";
        deviceType = 0;
        lock (_gate)
        {
            if (_initCount == 0) return LfxResult.NoInit;
            if (_getDeviceDescription == null) return LfxResult.Failure;
            const int size = 256; // LFX_MAX_STRING_SIZE 255 + NUL
            byte* buf = stackalloc byte[size];
            new Span<byte>(buf, size).Clear();
            byte type = 0;
            uint r = _getDeviceDescription(device, buf, size, &type);
            if (r == LfxResult.Success)
            {
                description = (Marshal.PtrToStringAnsi((IntPtr)buf) ?? "").Trim();
                deviceType = type;
            }
            return r;
        }
    }

    public uint GetNumLights(uint device, out uint count)
    {
        count = 0;
        lock (_gate)
        {
            if (_initCount == 0) return LfxResult.NoInit;
            uint c = 0;
            uint r = _getNumLights(device, &c);
            count = c;
            return r;
        }
    }

    public uint SetLightColor(uint device, uint light, in LfxColor color)
    {
        lock (_gate)
        {
            if (_initCount == 0) return LfxResult.NoInit;
            LfxColor c = color;
            return _setLightColor(device, light, &c);
        }
    }
}
