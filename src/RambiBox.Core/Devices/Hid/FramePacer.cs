using System.Diagnostics;

namespace RambiBox.Core.Devices;

/// <summary>
/// Latest-wins frame mailbox with constant-rate pacing and FPS measurement, shared by drivers whose link has no
/// measurable wire time (USB HID, vendor SDKs). <see cref="Post"/> may be called from any thread; <see cref="TryTake"/>
/// and <see cref="MarkWritten"/> only from the driver's own thread. Nothing allocates per frame (buffers grow only when
/// the LED count grows).
/// </summary>
internal sealed class FramePacer : IDisposable
{
    private static readonly long Freq = Stopwatch.Frequency;

    private readonly object _lock = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private byte[] _pending = [], _sending = [];
    private int _pendingLen, _sendingLen;
    private bool _hasPending;
    private long _nextAllowed;
    private long _intervalTicks;
    private double _frameRate;
    private HighResolutionTimer? _slotTimer;
    private int _disposed;

    // statistics (driver thread writes, any thread reads)
    private long _framesWritten, _lastWriteStart, _fpsWindowStart;
    private int _fpsCount;
    private double _fps;

    public FramePacer(double frameRate) => SetFrameRate(frameRate);

    public double FrameRate => Volatile.Read(ref _frameRate);
    public long IntervalTicks => Volatile.Read(ref _intervalTicks);
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public void SetFrameRate(double frameRate)
    {
        frameRate = Math.Clamp(double.IsFinite(frameRate) ? frameRate : 30, 0.5, 1000);
        Volatile.Write(ref _frameRate, frameRate);
        Volatile.Write(ref _intervalTicks, (long)(Freq / frameRate));
    }

    /// <summary>Copies the frame into the mailbox, replacing an unsent one, and wakes the driver thread.</summary>
    public void Post(ReadOnlySpan<byte> rgb)
    {
        lock (_lock)
        {
            if (_pending.Length < rgb.Length) _pending = new byte[rgb.Length]; // only when the LED count grows
            rgb.CopyTo(_pending);
            _pendingLen = rgb.Length;
            _hasPending = true;
        }
        Signal();
    }

    /// <summary>Wakes a driver thread blocked in <see cref="WaitForFrame"/>.</summary>
    public void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _frameReady.Set(); } catch (ObjectDisposedException) { /* racing Dispose */ }
    }

    /// <summary>Blocks the driver thread until a frame (or <see cref="Signal"/>) arrives or the timeout passes.</summary>
    public bool WaitForFrame(int timeoutMs)
    {
        try { return _frameReady.WaitOne(Math.Max(0, timeoutMs)); }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>Drops any unsent frame and restarts pacing and statistics (on connect).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _hasPending = false;
            _nextAllowed = Stopwatch.GetTimestamp();
        }
        _fpsCount = 0;
        _fpsWindowStart = Stopwatch.GetTimestamp();
        Volatile.Write(ref _fps, 0);
        Volatile.Write(ref _lastWriteStart, 0);
    }

    /// <summary>Drops an unsent frame (blackout).</summary>
    public void DropPending()
    {
        lock (_lock) _hasPending = false;
    }

    /// <summary>
    /// Driver thread: waits (high resolution) for the pacing slot, then takes the newest frame. Returns false when the
    /// mailbox is empty. The span stays valid until the next call.
    /// </summary>
    public bool TryTake(HighResolutionTimer timer, out ReadOnlySpan<byte> frame)
    {
        frame = default;
        long due;
        lock (_lock)
        {
            if (!_hasPending) return false;
            due = _nextAllowed;
        }
        if (Stopwatch.GetTimestamp() < due) timer.WaitUntil(due);
        lock (_lock)
        {
            if (!_hasPending) return false;
            (_pending, _sending) = (_sending, _pending);
            _sendingLen = _pendingLen;
            _hasPending = false;
            _nextAllowed = Stopwatch.GetTimestamp() + IntervalTicks;
        }
        frame = _sending.AsSpan(0, _sendingLen);
        return true;
    }

    /// <summary>Records a frame write that started at <paramref name="startTimestamp"/>; the next slot is one interval later.</summary>
    public void MarkWritten(long startTimestamp)
    {
        lock (_lock) _nextAllowed = startTimestamp + IntervalTicks;
        Volatile.Write(ref _lastWriteStart, startTimestamp);
        Interlocked.Increment(ref _framesWritten);
        long now = Stopwatch.GetTimestamp();
        _fpsCount++;
        long win = now - _fpsWindowStart;
        if (win >= Freq)
        {
            Volatile.Write(ref _fps, _fpsCount * (double)Freq / win);
            _fpsCount = 0;
            _fpsWindowStart = now;
        }
    }

    /// <summary>Measured frames per second; 0 when nothing was written for 2 s.</summary>
    public double FramesPerSecond
    {
        get
        {
            long last = Volatile.Read(ref _lastWriteStart);
            if (last == 0 || Stopwatch.GetTimestamp() - last > 2 * Freq) return 0;
            return Volatile.Read(ref _fps);
        }
    }

    /// <summary>
    /// <see cref="ILedDevice.WaitForSlot"/>: blocks until a new frame would be sent without queueing behind an unsent one.
    /// <paramref name="connected"/> is re-checked while waiting.
    /// </summary>
    public bool WaitForSlot(int timeoutMs, Func<bool> connected)
    {
        if (!connected() || Volatile.Read(ref _disposed) != 0) return false;
        long start = Stopwatch.GetTimestamp();
        long deadline = start + Math.Max(0, timeoutMs) * Freq / 1000;
        HighResolutionTimer? timer = null;
        try
        {
            while (true)
            {
                if (!connected()) return false;
                long now = Stopwatch.GetTimestamp();
                long slot;
                lock (_lock)
                {
                    slot = _nextAllowed;
                    if (_hasPending) slot = Math.Max(slot, now) + IntervalTicks;
                }
                if (now >= slot) return true;
                if (now >= deadline) return false;
                timer ??= Interlocked.Exchange(ref _slotTimer, null) ?? new HighResolutionTimer();
                // Wake at least every 50 ms so a disconnect is noticed.
                timer.WaitUntil(Math.Min(Math.Min(slot, deadline), now + Freq / 20));
            }
        }
        finally
        {
            if (timer is not null && Interlocked.CompareExchange(ref _slotTimer, timer, null) is not null) timer.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _frameReady.Dispose();
        Interlocked.Exchange(ref _slotTimer, null)?.Dispose();
    }
}
