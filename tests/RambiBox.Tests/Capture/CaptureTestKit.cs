using System.Diagnostics;
using System.Runtime.InteropServices;
using RambiBox.Core.Capture;
using RambiBox.Core.Config;

namespace RambiBox.Tests.Capture;

/// <summary>Helpers for the real-screen capture tests: a test pattern window, CPU cycle counters, GDI screen reads.</summary>
internal static class CaptureTestKit
{
    public const int WindowX = 40, WindowY = 40, WindowSize = 320;

    /// <summary>Zones of the test pattern window on <paramref name="monitor"/>: solid red, 1-px black/white stripes.</summary>
    public static (RectF Red, RectF Stripes) PatternZones(MonitorInfo monitor)
    {
        RectF Px(int x0, int y0, int x1, int y1) => new(
            (float)(WindowX + x0) / monitor.Width, (float)(WindowY + y0) / monitor.Height,
            (float)(WindowX + x1) / monitor.Width, (float)(WindowY + y1) / monitor.Height);
        return (Px(48, 32, 272, 128), Px(48, 192, 208, 288));
    }

    /// <summary>The realistic 112-zone layout plus the two test zones at the end.</summary>
    public static List<LedZone> ZonesWithPattern(MonitorInfo monitor)
    {
        var (red, stripes) = PatternZones(monitor);
        var zones = LayoutGenerator.Generate(new LayoutSettings());
        zones.Add(new LedZone(red.Left, red.Top, red.Right, red.Bottom));
        zones.Add(new LedZone(stripes.Left, stripes.Top, stripes.Right, stripes.Bottom));
        return zones;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [DllImport("kernel32.dll")] private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    public static ulong ThreadCycles()
    {
        QueryThreadCycleTime(GetCurrentThread(), out ulong c);
        return c;
    }

    public static ulong ProcessCycles()
    {
        QueryProcessCycleTime(GetCurrentProcess(), out ulong c);
        return c;
    }

    private static double _cyclesPerMs;

    /// <summary>Thread cycle counts are in TSC units; calibrate against wall time with a short busy loop.</summary>
    public static double CyclesPerMs()
    {
        if (_cyclesPerMs > 0) return _cyclesPerMs;
        ulong c0 = ThreadCycles();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 50) { }
        return _cyclesPerMs = (ThreadCycles() - c0) / sw.Elapsed.TotalMilliseconds;
    }

    // ---- Cursor

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    // ---- GDI screen read (what the user sees, incl. overlays like the capture border)

    /// <summary>Reads a screen rectangle (physical pixels, per-monitor DPI aware) as BGRA bytes via GDI BitBlt.</summary>
    public static byte[] ReadScreen(int x, int y, int w, int h)
    {
        var prev = SetThreadDpiAwarenessContext(new IntPtr(-4));
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        try
        {
            var old = SelectObject(mem, bmp);
            BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
            SelectObject(mem, old);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var data = new byte[w * h * 4];
            GetDIBits(mem, bmp, 0, (uint)h, data, ref bi, 0);
            return data;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            if (prev != IntPtr.Zero) SetThreadDpiAwarenessContext(prev);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

/// <summary>
/// Topmost GDI window drawing the test pattern on its own thread (physical pixels via PMv2 DPI awareness):
/// top half solid red, bottom-left 1-px black/white stripes (checks linear-light averaging), bottom-right animated
/// (forces new desktop frames). Records its own CPU cycles so benchmarks can subtract them.
/// </summary>
internal sealed class TestPatternWindow : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly ManualResetEventSlim _ready = new();
    private long _cycles;

    /// <param name="frameSleepMs">Pause between redraws (4 ms ≈ "video playing"; the monitor refresh limits it anyway).</param>
    public TestPatternWindow(int x = CaptureTestKit.WindowX, int y = CaptureTestKit.WindowY, int size = CaptureTestKit.WindowSize, int frameSleepMs = 4)
    {
        _thread = new Thread(() => Run(x, y, size, frameSleepMs)) { IsBackground = true, Name = "Test pattern" };
        _thread.Start();
        _ready.Wait(2000);
    }

    /// <summary>CPU cycles the drawing thread has used so far.</summary>
    public ulong Cycles => (ulong)Interlocked.Read(ref _cycles);

    private void Run(int x, int y, int size, int frameSleepMs)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4));
        var hwnd = CreateWindowExW(0x8 | 0x80 | 0x08000000, "STATIC", "", 0x80000000 | 0x10000000, x, y, size, size,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ready.Set();
        if (hwnd == IntPtr.Zero) return;
        var red = CreateSolidBrush(0x0000FF);
        var white = CreateSolidBrush(0xFFFFFF);
        var black = CreateSolidBrush(0x000000);
        int half = size / 2, split = size * 3 / 4;
        try
        {
            for (int frame = 0; !_stop; frame++)
            {
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                var dc = GetDC(hwnd);
                var r = new RECT { Left = 0, Top = 0, Right = size, Bottom = half };
                FillRect(dc, ref r, red);
                for (int c = 0; c < split; c++)
                {
                    r = new RECT { Left = c, Top = half, Right = c + 1, Bottom = size };
                    FillRect(dc, ref r, (c & 1) == 0 ? white : black);
                }
                int g = (frame * 9) & 0xFF;
                var anim = CreateSolidBrush((uint)(g | (g << 8) | (g << 16)));
                r = new RECT { Left = split, Top = half, Right = size, Bottom = size };
                FillRect(dc, ref r, anim);
                DeleteObject(anim);
                ReleaseDC(hwnd, dc);
                GdiFlush();
                Interlocked.Exchange(ref _cycles, (long)CaptureTestKit.ThreadCycles());
                Thread.Sleep(frameSleepMs);
            }
        }
        finally
        {
            DeleteObject(red); DeleteObject(white); DeleteObject(black);
            DestroyWindow(hwnd);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
