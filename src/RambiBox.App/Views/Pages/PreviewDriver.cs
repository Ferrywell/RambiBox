using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RambiBox.App.Controls;
using RambiBox.App.ViewModels;
using RambiBox.Core.Config;

namespace RambiBox.App.Views.Pages;

/// <summary>
/// Feeds a <see cref="LedPreview"/> with the screen picture and the LED colors at ~15 fps — only while its page is
/// loaded and the window is visible (the timer is stopped otherwise, so a hidden window costs nothing).
/// </summary>
internal sealed class PreviewDriver
{
    private readonly LedPreview _preview;
    private readonly MainViewModel _vm;
    private readonly FrameworkElement _page;
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;

    public PreviewDriver(FrameworkElement page, LedPreview preview, MainViewModel vm)
    {
        _page = page;
        _preview = preview;
        _vm = vm;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
        _timer.Tick += (_, _) => Tick();
        page.Loaded += (_, _) =>
        {
            _vm.ViewActiveChanged += OnViewActiveChanged;
            OnViewActiveChanged(_vm.IsViewActive);
            Tick();
        };
        page.Unloaded += (_, _) =>
        {
            _vm.ViewActiveChanged -= OnViewActiveChanged;
            _timer.Stop();
        };
    }

    private void OnViewActiveChanged(bool active)
    {
        if (active && _page.IsLoaded) _timer.Start();
        else _timer.Stop();
    }

    public void Tick()
    {
        var host = _vm.Host;
        var s = host.Settings;
        _preview.Zones = s.Layout.Zones;
        _preview.Colors = host.Engine.GetPreviewColors();
        _preview.Dimmed = !s.Enabled || s.Mode != LightMode.Ambilight;
        var mon = host.Sampler.CurrentMonitor;
        if (mon is { Width: > 0, Height: > 0 }) _preview.ScreenAspect = (double)mon.Width / mon.Height;

        try
        {
            if (host.Sampler.TryGetPreview(out var bgra, out int w, out int h) && w > 0 && h > 0 && bgra.Length >= w * h * 4)
            {
                if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
                    _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
                _preview.Screen = _bitmap;
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"Preview update failed: {ex.Message}");
            _timer.Stop();
        }
        _preview.Refresh();
    }
}
