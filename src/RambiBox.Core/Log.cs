using System.Collections.Concurrent;
using System.Text;

namespace RambiBox.Core;

/// <summary>
/// Tiny, allocation-light, thread-safe file logger (%LocalAppData%\RambiBox\logs\rambibox.log, rotated at 1 MB).
/// Writes happen on a background thread so callers on hot paths never block on disk I/O.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 2000);
    private static readonly Thread Writer;
    private const long MaxBytes = 1_000_000;

    /// <summary>%LocalAppData%\RambiBox\logs, or RAMBIBOX_LOG_DIR when set (tests use that to keep the real log clean).</summary>
    public static string Directory { get; } =
        Environment.GetEnvironmentVariable("RAMBIBOX_LOG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RambiBox", "logs");
    public static string FilePath { get; } = Path.Combine(Directory, "rambibox.log");

    /// <summary>Raised for every log line (UI may show the most recent ones). Invoked on the caller's thread.</summary>
    public static event Action<string>? LineLogged;

    static Log()
    {
        // Normal priority: the thread sleeps in the blocking queue almost always, but when something goes wrong
        // (capture stalls while the GPU / CPU is saturated) the warning must reach the file within a second instead of
        // being starved behind busy normal-priority threads. Each batch is appended + closed (flushed) right away.
        Writer = new Thread(WriteLoop) { IsBackground = true, Name = "RambiBox log writer", Priority = ThreadPriority.Normal };
        Writer.Start();
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        Queue.TryAdd(line); // drop when the queue is full rather than block
        try { LineLogged?.Invoke(line); } catch { /* never let a listener break logging */ }
    }

    /// <summary>Blocks until queued lines are written (call on shutdown).</summary>
    public static void Flush(int timeoutMs = 1000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Queue.Count > 0 && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
    }

    private static void WriteLoop()
    {
        var sb = new StringBuilder();
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            sb.Clear().AppendLine(line);
            while (Queue.TryTake(out var more)) sb.AppendLine(more);
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    File.Copy(FilePath, FilePath + ".1", overwrite: true);
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
