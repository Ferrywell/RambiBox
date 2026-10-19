using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RambiBox.Core.Capture;

/// <summary>An active display path: the monitor plus the ids needed for DisplayConfig queries.</summary>
internal sealed record DisplayTarget(
    MonitorInfo Info,
    DisplayConfigNative.LUID AdapterId,
    uint TargetId,
    int DxgiRotation,
    string EdidKey);

/// <summary>
/// Monitor enumeration via QueryDisplayConfig (fast, no D3D needed, DPI independent) with stable ids from
/// DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath (survives reboots; GDI names like \\.\DISPLAY27 don't).
/// </summary>
internal static class DisplayTopology
{
    private static readonly object CacheLock = new();
    private static DisplayTarget[] _cache = Array.Empty<DisplayTarget>();
    private static MonitorInfo[] _cacheInfos = Array.Empty<MonitorInfo>();
    private static long _cacheTimestamp;
    private static string? _lastError;

    /// <summary>Cached enumeration (refreshed when older than <paramref name="maxAgeMs"/>). Thread-safe.</summary>
    public static DisplayTarget[] GetTargets(int maxAgeMs = 500)
    {
        lock (CacheLock)
        {
            if (_cacheTimestamp != 0 && Stopwatch.GetElapsedTime(_cacheTimestamp).TotalMilliseconds < maxAgeMs)
                return _cache;
            Refresh();
            return _cache;
        }
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors(int maxAgeMs = 500)
    {
        lock (CacheLock)
        {
            GetTargets(maxAgeMs);
            return _cacheInfos;
        }
    }

    public static void Invalidate()
    {
        lock (CacheLock) _cacheTimestamp = 0;
    }

    private static void Refresh()
    {
        try
        {
            _cache = Enumerate();
            _cacheInfos = _cache.Select(t => t.Info).ToArray();
            _lastError = null;
        }
        catch (Exception ex)
        {
            if (_lastError != ex.Message) Log.Warn($"Display enumeration failed: {ex.Message}");
            _lastError = ex.Message;
        }
        _cacheTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Resolves a configured monitor id: exact id → same EDID model (if unique, e.g. after moving the cable to another
    /// port) → primary → first. Returns null if there is no monitor at all.
    /// </summary>
    public static DisplayTarget? Resolve(IReadOnlyList<DisplayTarget> targets, string? monitorId, out bool exact)
    {
        exact = false;
        if (targets.Count == 0) return null;
        if (!string.IsNullOrEmpty(monitorId))
        {
            foreach (var t in targets)
                if (string.Equals(t.Info.Id, monitorId, StringComparison.OrdinalIgnoreCase)) { exact = true; return t; }
            var key = EdidKeyFromId(monitorId);
            if (key is not null)
            {
                DisplayTarget? match = null; int count = 0;
                foreach (var t in targets)
                    if (string.Equals(t.EdidKey, key, StringComparison.OrdinalIgnoreCase)) { match = t; count++; }
                if (count == 1) return match;
            }
        }
        foreach (var t in targets) if (t.Info.IsPrimary) return t;
        return targets[0];
    }

    /// <summary>"\\?\DISPLAY#AUS27AF#5&amp;..." → "AUS27AF"; "edid:AUS27AF:..." → "AUS27AF".</summary>
    internal static string? EdidKeyFromId(string id)
    {
        var parts = id.Split('#');
        if (parts.Length >= 3 && parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return parts[1];
        if (id.StartsWith("edid:", StringComparison.OrdinalIgnoreCase))
        {
            var p = id.Split(':');
            if (p.Length >= 2) return p[1];
        }
        return null;
    }

    internal static string DecodeEdidManufacturer(ushort raw)
    {
        // Stored big-endian in the EDID; DisplayConfig hands the two bytes over as-is.
        int v = (ushort)((raw >> 8) | (raw << 8));
        char c1 = (char)('A' - 1 + ((v >> 10) & 31)), c2 = (char)('A' - 1 + ((v >> 5) & 31)), c3 = (char)('A' - 1 + (v & 31));
        return char.IsAsciiLetterUpper(c1) && char.IsAsciiLetterUpper(c2) && char.IsAsciiLetterUpper(c3)
            ? new string([c1, c2, c3]) : $"{raw:X4}";
    }

    private static unsafe DisplayTarget[] Enumerate()
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        DisplayConfigNative.PATH_INFO[] paths;
        DisplayConfigNative.MODE_INFO[] modes;
        uint np, nm;
        int err, attempts = 0;
        do
        {
            err = DisplayConfigNative.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm);
            if (err != 0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes error {err}");
            paths = new DisplayConfigNative.PATH_INFO[np];
            modes = new DisplayConfigNative.MODE_INFO[nm];
            fixed (DisplayConfigNative.PATH_INFO* pp = paths)
            fixed (DisplayConfigNative.MODE_INFO* pm = modes)
                err = DisplayConfigNative.QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, pp, ref nm, pm, IntPtr.Zero);
        } while (err == 122 /* ERROR_INSUFFICIENT_BUFFER */ && ++attempts < 5);
        if (err != 0) throw new InvalidOperationException($"QueryDisplayConfig error {err}");

        var result = new List<DisplayTarget>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < np; i++)
        {
            ref var path = ref paths[i];
            if ((path.flags & 1) == 0) continue; // DISPLAYCONFIG_PATH_ACTIVE

            string gdi = DisplayConfigNative.GetSourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
            if (gdi.Length == 0 || !seenSources.Add(gdi)) continue; // clone mode: first target wins

            uint mi = path.sourceInfo.modeInfoIdx;
            if (mi >= nm || modes[mi].infoType != 1 /* SOURCE */) continue;
            var sm = modes[mi].sourceMode;

            DisplayConfigNative.GetTargetName(path.targetInfo.adapterId, path.targetInfo.id, out var tn);
            string friendly = tn.FriendlyName;
            string devicePath = tn.DevicePath;
            string edidKey = (tn.flags & 2) != 0 /* edidIdsValid */
                ? DecodeEdidManufacturer(tn.edidManufactureId) + tn.edidProductCodeId.ToString("X4")
                : "";
            string id = devicePath.Length > 0 ? devicePath
                : edidKey.Length > 0 ? $"edid:{edidKey}:{tn.connectorInstance}:{path.targetInfo.outputTechnology}"
                : $"gdi:{gdi}";

            int w = (int)sm.width, h = (int)sm.height;
            int rot = (int)path.targetInfo.rotation; // 1..4, same values as DXGI_MODE_ROTATION
            if (string.IsNullOrWhiteSpace(friendly))
                friendly = path.targetInfo.outputTechnology == 0x80000000 /* INTERNAL */ ? "Built-in display" : gdi.TrimStart('\\', '.');
            var rr = path.targetInfo.refreshRate;
            double hz = rr.Denominator == 0 ? 0 : (double)rr.Numerator / rr.Denominator;
            bool hdr = DisplayConfigNative.IsHdrActive(path.targetInfo.adapterId, path.targetInfo.id);
            var info = new MonitorInfo(id, $"{friendly} ({w}×{h})", gdi, sm.position.x, sm.position.y, w, h,
                IsPrimary: sm.position.x == 0 && sm.position.y == 0, IsHdr: hdr, RefreshHz: Math.Round(hz, 2));
            result.Add(new DisplayTarget(info, path.targetInfo.adapterId, path.targetInfo.id, rot, edidKey));
        }
        // Primary first, then left-to-right, so the UI list is stable.
        return result.OrderByDescending(t => t.Info.IsPrimary).ThenBy(t => t.Info.X).ThenBy(t => t.Info.Y).ToArray();
    }
}

/// <summary>Blittable P/Invoke declarations for the Windows CCD (Connecting and Configuring Displays) API.</summary>
internal static unsafe class DisplayConfigNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID : IEquatable<LUID>
    {
        public uint LowPart;
        public int HighPart;
        public readonly bool Equals(LUID o) => LowPart == o.LowPart && HighPart == o.HighPart;
        public override readonly bool Equals(object? obj) => obj is LUID o && Equals(o);
        public override readonly int GetHashCode() => HashCode.Combine(LowPart, HighPart);
        public override readonly string ToString() => $"{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SOURCE_MODE { public uint width, height, pixelFormat; public POINTL position; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public SOURCE_MODE sourceMode;
        [FieldOffset(16)] private ulong _unionAlign; // union contains a UINT64 → 8-byte alignment
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public fixed char viewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        public fixed char monitorFriendlyDeviceName[64];
        public fixed char monitorDevicePath[128];

        public string FriendlyName { get { fixed (char* p = monitorFriendlyDeviceName) return new string(p); } }
        public string DevicePath { get { fixed (char* p = monitorDevicePath) return new string(p); } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ADVANCED_COLOR_INFO
    {
        public DEVICE_INFO_HEADER header;
        public uint value; // bit0 supported, bit1 enabled, bit2 wideColorEnforced, bit3 forceDisabled
        public uint colorEncoding, bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDR_WHITE_LEVEL { public DEVICE_INFO_HEADER header; public uint SDRWhiteLevel; }

    public const uint GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2, GET_ADVANCED_COLOR_INFO = 9, GET_SDR_WHITE_LEVEL = 11;

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, PATH_INFO* pathArray,
        ref uint numModeInfoArrayElements, MODE_INFO* modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int DisplayConfigGetDeviceInfo(DEVICE_INFO_HEADER* requestPacket);

    public static string GetSourceName(LUID adapter, uint sourceId)
    {
        SOURCE_DEVICE_NAME s = default;
        s.header = new DEVICE_INFO_HEADER { type = GET_SOURCE_NAME, size = (uint)sizeof(SOURCE_DEVICE_NAME), adapterId = adapter, id = sourceId };
        if (DisplayConfigGetDeviceInfo(&s.header) != 0) return "";
        return new string(s.viewGdiDeviceName);
    }

    public static bool GetTargetName(LUID adapter, uint targetId, out TARGET_DEVICE_NAME t)
    {
        t = default;
        t.header = new DEVICE_INFO_HEADER { type = GET_TARGET_NAME, size = (uint)sizeof(TARGET_DEVICE_NAME), adapterId = adapter, id = targetId };
        fixed (TARGET_DEVICE_NAME* p = &t) return DisplayConfigGetDeviceInfo(&p->header) == 0;
    }

    /// <summary>
    /// HDR (advanced color) active on the target. Advanced color with "wide color enforced" is ACM on an SDR display,
    /// which is not HDR.
    /// </summary>
    public static bool IsHdrActive(LUID adapter, uint targetId)
    {
        ADVANCED_COLOR_INFO a = default;
        a.header = new DEVICE_INFO_HEADER { type = GET_ADVANCED_COLOR_INFO, size = (uint)sizeof(ADVANCED_COLOR_INFO), adapterId = adapter, id = targetId };
        if (DisplayConfigGetDeviceInfo(&a.header) != 0) return false;
        bool enabled = (a.value & 2) != 0, wideColorEnforced = (a.value & 4) != 0;
        return enabled && !wideColorEnforced;
    }

    /// <summary>SDR white level in nits (allocation free), or null when unavailable.</summary>
    public static float? GetSdrWhiteNits(LUID adapter, uint targetId)
    {
        SDR_WHITE_LEVEL s = default;
        s.header = new DEVICE_INFO_HEADER { type = GET_SDR_WHITE_LEVEL, size = (uint)sizeof(SDR_WHITE_LEVEL), adapterId = adapter, id = targetId };
        if (DisplayConfigGetDeviceInfo(&s.header) != 0 || s.SDRWhiteLevel == 0) return null;
        return ColorMath.SdrWhiteLevelToNits(s.SDRWhiteLevel);
    }

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly IntPtr DpiPerMonitorV2 = new(-4);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
