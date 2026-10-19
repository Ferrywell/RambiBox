using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using QRCoder;
using RambiBox.App.Services;
using RambiBox.Core;
using RambiBox.Core.Calibration;
using RambiBox.Core.Capture;

namespace RambiBox.App.Views;

/// <summary>
/// Full-screen, top-most window on the captured monitor that shows the phone-calibration pattern and QR code.
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly AppHost _host;
    private readonly PhoneCalibrationService? _service;
    private readonly MonitorInfo? _monitor;
    private readonly CalibrationSnapshot _before;
    private CalibrationPattern? _drawnPattern;
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
        SizeChanged += (_, _) => { _drawnPattern = null; Render(); };
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

    private void OnServiceChanged()
    {
        // any thread → coalesce onto the UI thread
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
        bool finished = false;
        if (_service is not null && _startError is null)
        {
            try
            {
                state = _service.State;
                pattern = _service.Pattern;
                url = _service.Url;
                finished = _service.IsFinished;
            }
            catch (Exception ex)
            {
                Log.Error("Calibration state unavailable", ex);
                _startError = $"The calibration stopped working: {ex.Message}";
            }
        }

        if (finished)
        {
            Finish(keepResult: true);
            return;
        }

        // ---- pattern
        if (pattern is not null && !ReferenceEquals(pattern, _drawnPattern))
        {
            _drawnPattern = pattern;
            PatternCanvas.Children.Clear();
            foreach (var r in pattern.Rects)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(0, (r.Right - r.Left) * w),
                    Height = Math.Max(0, (r.Bottom - r.Top) * h),
                    Fill = new SolidColorBrush(Color.FromRgb((byte)(r.Rgb >> 16), (byte)(r.Rgb >> 8), (byte)r.Rgb)),
                    SnapsToDevicePixels = true,
                };
                Canvas.SetLeft(rect, r.Left * w);
                Canvas.SetTop(rect, r.Top * h);
                PatternCanvas.Children.Add(rect);
            }
        }

        // ---- QR
        bool showQr = pattern is { ShowQr: true } && url is not null && state?.Phase != CalibrationPhase.Failed;
        QrPanel.Visibility = showQr ? Visibility.Visible : Visibility.Collapsed;
        if (showQr) LayoutQr(pattern!, url!, w, h);

        // ---- status (between the top band and the centre) and buttons (above the bottom band)
        StatusPanel.Margin = new Thickness(24, h * 0.095, 24, 0);
        StatusPanel.MaxWidth = Math.Max(300, w * 0.7);
        ButtonPanel.Margin = new Thickness(0, 0, 0, h * 0.11);
        StatusText.FontSize = Math.Clamp(h * 0.022, 16, 30);
        DetailText.FontSize = Math.Clamp(h * 0.013, 13, 20);
        HintText.FontSize = Math.Clamp(h * 0.012, 12, 18);
        CancelButton.FontSize = DoneButton.FontSize = Math.Clamp(h * 0.012, 14, 20);
        CancelButton.MinWidth = DoneButton.MinWidth = CancelButton.FontSize * 9;

        if (_startError is not null)
        {
            StatusText.Text = _startError;
            DetailText.Text = "";
            HintText.Visibility = Visibility.Collapsed;
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
        var parts = new List<string>();
        if (state.PhotosAnalyzed > 0) parts.Add(state.PhotosAnalyzed == 1 ? "1 photo" : $"{state.PhotosAnalyzed} photos");
        if (state.ErrorPercent is { } err) parts.Add($"color difference {err:0.0}%");
        parts.Add($"white balance R {state.WhiteR:P0} · G {state.WhiteG:P0} · B {state.WhiteB:P0}");
        if (_service!.IsDarkStage) parts.Add("dark-tone step");
        DetailText.Text = string.Join("   ·   ", parts);
        HintText.Visibility = state.Phase is CalibrationPhase.WaitingForPhone or CalibrationPhase.Failed
            ? Visibility.Visible : Visibility.Collapsed;
        bool failed = state.Phase == CalibrationPhase.Failed;
        StatusText.Foreground = failed ? new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0xA4)) : new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8));
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
        Canvas.SetLeft(QrPanel, left);
        Canvas.SetTop(QrPanel, top + Math.Max(0, (ah - (side + textH)) / 2));
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
            var (wb, g) = (_before.WhiteBalance, _before.ChannelGamma);
            _host.ApplyWhiteBalance(wb.R, wb.G, wb.B);
            _host.ApplyChannelGamma(g.R, g.G, g.B);
        }
        else if (_started)
        {
            // remember the old values for "Restore previous white balance" (applied values are read on the UI thread)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_host.CurrentCalibration != _before) _host.PreviousCalibration = _before;
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
