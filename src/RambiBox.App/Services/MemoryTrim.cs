using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using RambiBox.Core;

namespace RambiBox.App.Services;

/// <summary>
/// Gives memory back to Windows when RambiBox goes back to living in the tray: compacts the managed heap and
/// trims the working set, so Task Manager shows the app's real idle footprint instead of what the window used.
/// </summary>
internal static class MemoryTrim
{
    private static DispatcherTimer? _pending;

    /// <summary>Trims after <paramref name="delay"/>; repeated calls restart the delay. Call on the UI thread.</summary>
    public static void Schedule(TimeSpan delay)
    {
        _pending?.Stop();
        _pending = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = delay };
        _pending.Tick += (_, _) =>
        {
            _pending?.Stop();
            _pending = null;
            TrimNow();
        };
        _pending.Start();
    }

    public static void Cancel()
    {
        _pending?.Stop();
        _pending = null;
    }

    public static void TrimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            Log.Info($"Memory trimmed: working set {p.WorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB");
        }
        catch (Exception ex)
        {
            Log.Warn($"Memory trim failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
