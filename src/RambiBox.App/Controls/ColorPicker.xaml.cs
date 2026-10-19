using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RambiBox.App.Controls;

/// <summary>HSV color picker: saturation/brightness square, hue bar, hex box and preset swatches.</summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((ColorPicker)d).OnSelectedColorChanged((Color)e.NewValue)));

    private static readonly (string Name, Color Color)[] PresetColors =
    [
        ("Warm white (2700 K)", Color.FromRgb(0xFF, 0xA7, 0x57)),
        ("Soft white (3500 K)", Color.FromRgb(0xFF, 0xC4, 0x89)),
        ("Daylight white", Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ("Red", Color.FromRgb(0xFF, 0x00, 0x00)),
        ("Orange", Color.FromRgb(0xFF, 0x60, 0x00)),
        ("Amber", Color.FromRgb(0xFF, 0xB0, 0x00)),
        ("Yellow", Color.FromRgb(0xFF, 0xE6, 0x00)),
        ("Green", Color.FromRgb(0x00, 0xFF, 0x30)),
        ("Teal", Color.FromRgb(0x00, 0xFF, 0xB4)),
        ("Cyan", Color.FromRgb(0x00, 0xD8, 0xFF)),
        ("Blue", Color.FromRgb(0x00, 0x40, 0xFF)),
        ("Purple", Color.FromRgb(0x8A, 0x00, 0xFF)),
        ("Magenta", Color.FromRgb(0xFF, 0x00, 0xD0)),
        ("Pink", Color.FromRgb(0xFF, 0x30, 0x80)),
    ];

    private double _h, _s = 1, _v = 1;
    private bool _internal;

    public ColorPicker()
    {
        InitializeComponent();
        foreach (var (name, color) in PresetColors)
        {
            var b = new Button
            {
                Style = (Style)FindResource("SwatchButton"),
                Background = new SolidColorBrush(color),
                ToolTip = name,
            };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => SelectedColor = color;
            Presets.Children.Add(b);
        }
        SvArea.SizeChanged += (_, _) => UpdateVisuals();
        HueArea.SizeChanged += (_, _) => UpdateVisuals();
        Loaded += (_, _) => OnSelectedColorChanged(SelectedColor);
    }

    public Color SelectedColor { get => (Color)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }

    private void OnSelectedColorChanged(Color c)
    {
        if (!_internal)
        {
            var (h, s, v) = ToHsv(c);
            // keep the hue when the color is grey/black so the square doesn't jump to red
            if (s > 0.001 && v > 0.001) _h = h;
            if (v > 0.001) _s = s;
            _v = v;
        }
        UpdateVisuals();
    }

    private void SetFromHsv()
    {
        _internal = true;
        try { SelectedColor = FromHsv(_h, _s, _v); }
        finally { _internal = false; }
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        HueFill.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
        var c = SelectedColor;
        PreviewSwatch.Background = new SolidColorBrush(c);
        if (!HexBox.IsKeyboardFocusWithin) HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        double w = SvArea.ActualWidth - 2, h = SvArea.ActualHeight - 2;
        Canvas.SetLeft(SvThumb, _s * w - 9);
        Canvas.SetTop(SvThumb, (1 - _v) * h - 9);
        Canvas.SetLeft(SvThumbInner, _s * w - 8);
        Canvas.SetTop(SvThumbInner, (1 - _v) * h - 8);
        SvThumbInner.Fill = new SolidColorBrush(c);

        double hw = HueArea.ActualWidth;
        Canvas.SetLeft(HueThumb, _h / 360 * hw - 10);
        Canvas.SetTop(HueThumb, 0);
        HueThumb.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
    }

    // ---- square
    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        SvArea.Focus();
        SvArea.CaptureMouse();
        SvFromPoint(e.GetPosition(SvArea));
        e.Handled = true;
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (SvArea.IsMouseCaptured) SvFromPoint(e.GetPosition(SvArea));
    }

    private void SvFromPoint(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
        _v = Math.Clamp(1 - p.Y / Math.Max(1, SvArea.ActualHeight), 0, 1);
        SetFromHsv();
    }

    private void OnSvKey(object sender, KeyEventArgs e)
    {
        const double step = 0.02;
        switch (e.Key)
        {
            case Key.Left: _s = Math.Max(0, _s - step); break;
            case Key.Right: _s = Math.Min(1, _s + step); break;
            case Key.Up: _v = Math.Min(1, _v + step); break;
            case Key.Down: _v = Math.Max(0, _v - step); break;
            default: return;
        }
        SetFromHsv();
        e.Handled = true;
    }

    // ---- hue bar
    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        HueArea.Focus();
        HueArea.CaptureMouse();
        HueFromPoint(e.GetPosition(HueArea));
        e.Handled = true;
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (HueArea.IsMouseCaptured) HueFromPoint(e.GetPosition(HueArea));
    }

    private void HueFromPoint(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(1, HueArea.ActualWidth), 0, 1) * 359.9;
        if (_s < 0.05 && _v > 0.05) _s = 1; // picking a hue on white should show that hue
        SetFromHsv();
    }

    private void OnHueKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: _h = (_h + 355) % 360; break;
            case Key.Right: _h = (_h + 5) % 360; break;
            default: return;
        }
        SetFromHsv();
        e.Handled = true;
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    // ---- hex
    private void OnHexKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitHex();
            HexBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnHexCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        var t = HexBox.Text.Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(ch => $"{ch}{ch}"));
        if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            SelectedColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var c = SelectedColor;
        HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // ---- conversions
    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360 / 60;
        int i = (int)h;
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
