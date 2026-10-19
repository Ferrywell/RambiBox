using RambiBox.Core;

namespace RambiBox.App.Services;

/// <summary>Keeps the most recent log lines in memory so the Device page can show a log tail instantly.</summary>
public sealed class LogBuffer : IDisposable
{
    private const int Capacity = 200;
    private readonly Queue<string> _lines = new(Capacity);
    private readonly object _lock = new();

    /// <summary>Raised on the logging thread for each new line.</summary>
    public event Action<string>? LineAdded;

    public LogBuffer() => Log.LineLogged += OnLine;

    private void OnLine(string line)
    {
        lock (_lock)
        {
            if (_lines.Count >= Capacity) _lines.Dequeue();
            _lines.Enqueue(line);
        }
        LineAdded?.Invoke(line);
    }

    public string[] Snapshot(int max)
    {
        lock (_lock) return _lines.Skip(Math.Max(0, _lines.Count - max)).ToArray();
    }

    public void Dispose() => Log.LineLogged -= OnLine;
}
