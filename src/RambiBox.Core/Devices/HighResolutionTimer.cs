using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RambiBox.Core.Devices;

/// <summary>
/// Sleeps until a Stopwatch timestamp with ~0.5 ms precision using a high-resolution waitable timer
/// (Windows 10 1803+), independent of the global 15.6 ms timer tick. One instance per waiting thread.
/// </summary>
internal sealed class HighResolutionTimer : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint INFINITE = 0xFFFFFFFF;

    private readonly SafeWaitHandle _handle;
    private readonly bool _highRes;

    public HighResolutionTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        _highRes = !_handle.IsInvalid;
        if (!_highRes)
        {
            _handle.Dispose();
            _handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, TIMER_ALL_ACCESS);
        }
    }

    public bool IsHighResolution => _highRes;

    /// <summary>Blocks until <paramref name="stopwatchTimestamp"/> (Stopwatch.GetTimestamp() units).</summary>
    public void WaitUntil(long stopwatchTimestamp)
    {
        long remaining = stopwatchTimestamp - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        if (_handle.IsInvalid || _handle.IsClosed)
        {
            Thread.Sleep(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
            return;
        }
        // Relative due time in 100 ns units (negative = relative).
        long due = -Math.Max(1, (long)(remaining * (10_000_000.0 / Stopwatch.Frequency)));
        if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            Thread.Sleep(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
            return;
        }
        WaitForSingleObject(_handle, INFINITE);
        if (!_highRes)
        {
            // Low-resolution fallback: finish with a short spin.
            while (Stopwatch.GetTimestamp() < stopwatchTimestamp) Thread.SpinWait(50);
        }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine,
        IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
