using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RambiBox.App.Controls;

/// <summary>
/// Indeterminate Windows 11 style progress ring (the WPF Fluent theme has none). Spins only while visible, so a
/// collapsed ring costs nothing. Template in Themes/Styles.xaml (part "PART_Arc" with a RotateTransform).
/// </summary>
public sealed class ProgressRing : Control
{
    private RotateTransform? _spin;

    static ProgressRing()
    {
        FocusableProperty.OverrideMetadata(typeof(ProgressRing), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ProgressRing), new FrameworkPropertyMetadata(false));
    }

    public ProgressRing()
    {
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _spin = null;
        if (GetTemplateChild("PART_Arc") is UIElement arc)
        {
            arc.RenderTransformOrigin = new Point(0.5, 0.5);
            arc.RenderTransform = _spin = new RotateTransform(); // own instance: template values are frozen
        }
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (_spin is null) return;
        if (IsVisible)
            _spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}
