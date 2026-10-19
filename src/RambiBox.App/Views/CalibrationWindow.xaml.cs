using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using RambiBox.App.Services;
using RambiBox.Core;
using RambiBox.Core.Calibration;
using RambiBox.Core.Capture;

namespace RambiBox.App.Views;

/// <summary>
/// Full-screen, top-most window on the captured monitor that shows the phone-calibration patterns (they change about
/// once a second during the color loop) and, until the phone connects, the QR code.
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly AppHost _host;
    private readonly PhoneCalibrationService? _service;
    private readonly MonitorInfo? _monitor;
    private readonly CalibrationSnapshot _before;
    private Uri? _qrUrl;
    private int _updateQueued;
    private bool _stopped, _started;
    private string? _startError;

    public CalibrationWindow(AppHost host, PhoneCalibrationService? service)
    {
        InitializeComponent();
        _host = host;
        _service = service;
        _before = host.CurrentCalibration;
        try
        {
            _monitor = host.Sampler.CurrentMonitor
                       ?? host.Sampler.GetMonitors().FirstOrDefault(m => m.IsPrimary)
                       ?? host.Sampler.GetMonitors().FirstOrDefault();
        }
        catch (Exception ex) { Log.Warn($"Calibration: no monitor info: {ex.Message}"); }

        SourceInitialized += (_, _) => PlaceOnMonitor();
        DpiChanged += (_, _) => PlaceOnMonitor();
        Loaded += (_, _) => StartService();
        SizeChanged += (_, _) => Render();
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>Exact placement in physical pixels on the captured monitor (works for any DPI / monitor layout).</summary>
    private void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (_monitor is { Width: > 0, Height: > 0 } m)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, m.X, m.Y, m.Width, m.Height, NativeMethods.SWP_FRAMECHANGED);
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void StartService()
    {
        if (_service is null)
        {
            _startError = "Phone calibration is not available in this version of RambiBox.";
            Render();
            return;
        }
        _host.SetCalibrationActive(true); // clears other test patterns first
        _service.Changed += OnServiceChanged;
        try
        {
            _started = _service.Start();
            Log.Info(_started ? "Calibration window: service started" : "Calibration window: service failed to start");
        }
        catch (Exception ex)
        {
            Log.Error("Calibration could not start", ex);
            _startError = $"The calibration could not start: {ex.Message}";
        }
        Render();
    }

    private static readonly SolidColorBrush StatusBrush = Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8));
    private static readonly SolidColorBrush FailedBrush = Frozen(Color.FromRgb(0xFF, 0x99, 0xA4));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnServiceChanged()
    {
        // any thread → coalesce onto the UI thread (the screen-gain loop may change the pattern with every analyzed
        // frame, ~2×/s; at most one render is queued at a time and the surface only redraws for a new pattern object)
        if (Interlocked.Exchange(ref _updateQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _updateQueued, 0);
            Render();
        });
    }

    private void Render()
    {
        if (_stopped) return;
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w < 10 || h < 10) return;

        CalibrationState? state = null;
        CalibrationPattern? pattern = null;
        Uri? url = null;
        bool finished = false, cancel = false;
        if (_service is not null && _startError is null)
        {
            try
            {
                state = _service.State;
                pattern = _service.Pattern;
                url = _service.Url;
                finished = _service.IsFinished;
                cancel = _service.IsCancelRequested;
            }
            catch (Exception ex)
            {
                Log.Error("Calibration state unavailable", ex);
                _startError = $"The calibration stopped working: {ex.Message}";
            }
        }

        if (cancel)
        {
            Finish(keepResult: false);
            return;
        }
        if (finished)
        {
            Finish(keepResult: true);
            return;
        }

        // ---- pattern (the surface only redraws when the pattern object changes)
        if (pattern is not null) Surface.Pattern = pattern;
        bool eye = pattern?.EyeMode == true;

        // ---- QR
        bool showQr = pattern is { ShowQr: true } && url is not null && state?.Phase != CalibrationPhase.Failed;
        QrPanel.Visibility = showQr ? Visibility.Visible : Visibility.Collapsed;
        if (showQr) LayoutQr(pattern!, url!, w, h);

        // ---- status: camera patterns keep it between the top band (≤ 0.075 h) / the detector's check line (0.125 h)
        // and the black probe (≥ 0.28 h); eye patterns have a black centre. Buttons sit above the bottom band.
        StatusPanel.VerticalAlignment = eye ? VerticalAlignment.Center : VerticalAlignment.Top;
        StatusPanel.Margin = eye ? new Thickness(w * 0.12, 0, w * 0.12, h * 0.08) : new Thickness(24, h * 0.135, 24, 0);
        StatusPanel.MaxWidth = Math.Max(300, w * (eye ? 0.6 : 0.62));
        ButtonPanel.Margin = new Thickness(0, 0, 0, h * (eye ? 0.17 : 0.13));
        StatusText.FontSize = Math.Clamp(h * (eye ? 0.026 : 0.019), 15, 30);
        DetailText.FontSize = Math.Clamp(h * 0.012, 12, 18);
        HintText.FontSize = Math.Clamp(h * 0.011, 11, 16);
        CancelButton.FontSize = DoneButton.FontSize = Math.Clamp(h * 0.012, 14, 20);
        CancelButton.MinWidth = DoneButton.MinWidth = CancelButton.FontSize * 9;

        if (_startError is not null)
        {
            StatusText.Text = _startError;
            DetailText.Text = "";
            HintText.Visibility = Visibility.Collapsed;
            ProgressTrack.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            return;
        }
        if (state is null)
        {
            StatusText.Text = "Starting…";
            return;
        }

        StatusText.Text = state.Message;
        DetailText.Text = state.Detail;
        DetailText.Visibility = string.IsNullOrEmpty(state.Detail) ? Visibility.Collapsed : Visibility.Visible;
        if (state.Progress is { } p && state.Phase == CalibrationPhase.LiveCamera)
        {
            ProgressTrack.Visibility = Visibility.Visible;
            ProgressTrack.Width = Math.Min(StatusPanel.MaxWidth, w * 0.4);
            ProgressFill.Width = ProgressTrack.Width * Math.Clamp(p, 0, 1);
        }
        else ProgressTrack.Visibility = Visibility.Collapsed;
        HintText.Visibility = state.Phase is CalibrationPhase.WaitingForPhone or CalibrationPhase.Failed
            ? Visibility.Visible : Visibility.Collapsed;
        bool failed = state.Phase == CalibrationPhase.Failed;
        StatusText.Foreground = failed ? FailedBrush : StatusBrush;
        DoneButton.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = failed ? "Close" : "Cancel";
    }

    private void LayoutQr(CalibrationPattern p, Uri url, double w, double h)
    {
        double left = p.QrLeft * w, top = p.QrTop * h, aw = (p.QrRight - p.QrLeft) * w, ah = (p.QrBottom - p.QrTop) * h;
        if (aw < 40 || ah < 40) return;
        if (_qrUrl != url)
        {
            _qrUrl = url;
            QrImage.Source = CreateQrBitmap(url.ToString());
            UrlText.Text = url.ToString();
        }
        QrTitle.FontSize = Math.Clamp(ah * 0.075, 16, 40);
        UrlText.FontSize = Math.Clamp(ah * 0.032, 12, 18);
        double textH = QrTitle.FontSize * 1.4 + UrlText.FontSize * 1.4 + 20;
        double side = Math.Max(60, Math.Min(aw, ah - textH));

        // whole device pixels per module so the code stays perfectly crisp
        if (QrImage.Source is BitmapSource bmp && bmp.PixelWidth > 0)
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int modules = bmp.PixelWidth;
            int pxPerModule = Math.Max(1, (int)Math.Floor(side * scale / modules));
            side = pxPerModule * modules / scale;
        }
        QrImage.Width = QrImage.Height = side;
        QrPanel.Width = aw;
        System.Windows.Controls.Canvas.SetLeft(QrPanel, left);
        System.Windows.Controls.Canvas.SetTop(QrPanel, top + Math.Max(0, (ah - (side + textH)) / 2));
    }

    /// <summary>One pixel per module (incl. the 4-module quiet zone), black on white.</summary>
    private static BitmapSource CreateQrBitmap(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        int n = matrix.Count;
        var pixels = new byte[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                pixels[y * n + x] = matrix[y][x] ? (byte)0 : (byte)255;
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Gray8, null, pixels, n);
        bmp.Freeze();
        return bmp;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void OnDone(object sender, RoutedEventArgs e) => Finish(keepResult: true);

    /// <summary>Stops the calibration and restores the previous colors.</summary>
    public void Cancel() => Finish(keepResult: false);

    private void Finish(bool keepResult)
    {
        StopService(keepResult);
        Close();
    }

    private void StopService(bool keepResult)
    {
        if (_stopped) return;
        _stopped = true;
        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
            try { _service.Stop(keepResult); }
            catch (Exception ex) { Log.Error("Stopping the calibration failed", ex); }
        }
        if (!keepResult)
        {
            // make sure the old values are back even if the service could not restore them
            _host.ApplyCalibration(_before);
        }
        else if (_started)
        {
            // remember the old values for "Restore previous" (applied values are read on the UI thread)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!_host.CurrentCalibration.SameValues(_before)) _host.PreviousCalibration = _before;
                _host.FlushSave();
            });
        }
        _host.SetCalibrationActive(false);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Alt+F4 or closing from the taskbar = cancel
        if (!_stopped) StopService(keepResult: false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _service?.Dispose(); } catch { /* already stopped */ }
    }
}

/// <summary>
/// Draws a <see cref="CalibrationPattern"/> (black background + filled rectangles) in one element, with every edge
/// snapped to whole device pixels and no anti-aliasing, so the step code and the bands stay crisp for the camera.
/// </summary>
public sealed class PatternSurface : FrameworkElement
{
    private CalibrationPattern? _pattern;
    private readonly Dictionary<int, SolidColorBrush> _brushes = new();

    public PatternSurface()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
    }

    public CalibrationPattern? Pattern
    {
        get => _pattern;
        set
        {
            if (ReferenceEquals(value, _pattern)) return;
            _pattern = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
        if (_pattern is null || w <= 0 || h <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        double pw = Math.Round(w * sx), ph = Math.Round(h * sy);
        foreach (var r in _pattern.Rects)
        {
            // normalized → device pixels (rounded) → DIPs
            double x0 = Math.Round(r.Left * pw) / sx, x1 = Math.Round(r.Right * pw) / sx;
            double y0 = Math.Round(r.Top * ph) / sy, y1 = Math.Round(r.Bottom * ph) / sy;
            if (x1 <= x0 || y1 <= y0) continue;
            dc.DrawRectangle(Brush(r.Rgb), null, new Rect(x0, y0, x1 - x0, y1 - y0));
        }
    }

    private SolidColorBrush Brush(int rgb)
    {
        if (_brushes.TryGetValue(rgb, out var b)) return b;
        b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        if (_brushes.Count > 64) _brushes.Clear();
        _brushes[rgb] = b;
        return b;
    }
}
