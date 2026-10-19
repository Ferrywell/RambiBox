using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace RambiBox.App.Controls;

/// <summary>
/// A settings row: title + one-sentence description, a slider, the live value and a reset-to-default button.
/// </summary>
public partial class SettingSlider : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SettingSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SettingSlider)d).SyncFromValue()));
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingSlider), new PropertyMetadata("", (d, _) => ((SettingSlider)d).SyncTexts()));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingSlider), new PropertyMetadata("", (d, _) => ((SettingSlider)d).SyncTexts()));

    private bool _syncing;

    public SettingSlider()
    {
        InitializeComponent();
        PART_Slider.SmallChange = 1;
        PART_Slider.LargeChange = 10;
        Loaded += (_, _) => { SyncTexts(); SyncFromValue(); };
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public double Minimum { get => PART_Slider.Minimum; set => PART_Slider.Minimum = value; }
    public double Maximum { get => PART_Slider.Maximum; set => PART_Slider.Maximum = value; }
    public double SmallChange { get => PART_Slider.SmallChange; set => PART_Slider.SmallChange = value; }
    public double LargeChange { get => PART_Slider.LargeChange; set => PART_Slider.LargeChange = value; }
    public bool IsDirectionReversed { get => PART_Slider.IsDirectionReversed; set => PART_Slider.IsDirectionReversed = value; }

    /// <summary>Snap step (e.g. 1 for whole numbers, 0.01 for gamma). 0 = no snapping.</summary>
    public double Step
    {
        get => PART_Slider.TickFrequency;
        set
        {
            PART_Slider.TickFrequency = value;
            PART_Slider.IsSnapToTickEnabled = value > 0;
            PART_Slider.TickPlacement = System.Windows.Controls.Primitives.TickPlacement.None;
        }
    }

    /// <summary>Value used by the reset button. NaN hides the button.</summary>
    public double DefaultValue { get; set; } = double.NaN;

    /// <summary>Composite format for the value, e.g. "{0:0}%" or "{0:0} ms".</summary>
    public string ValueFormat { get; set; } = "{0:0}";

    /// <summary>Optional custom formatter (overrides <see cref="ValueFormat"/>).</summary>
    public Func<double, string>? Formatter { get; set; }

    public string LeftLabel { get => LeftText.Text; set { LeftText.Text = value; SyncTexts(); } }
    public string RightLabel { get => RightText.Text; set { RightText.Text = value; SyncTexts(); } }

    private void SyncTexts()
    {
        HeaderText.Text = Header;
        DescriptionText.Text = Description;
        DescriptionText.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        EndLabels.Visibility = string.IsNullOrEmpty(LeftText.Text) && string.IsNullOrEmpty(RightText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(PART_Slider, Header);
        AutomationProperties.SetHelpText(PART_Slider, Description);
        ResetButton.Visibility = double.IsNaN(DefaultValue) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SyncFromValue()
    {
        _syncing = true;
        try { PART_Slider.Value = Value; }
        finally { _syncing = false; }
        UpdateValueText();
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing && Math.Abs(Value - e.NewValue) > 1e-9) Value = e.NewValue;
        UpdateValueText();
    }

    private void UpdateValueText()
    {
        double v = PART_Slider.Value;
        ValueText.Text = Formatter?.Invoke(v) ?? string.Format(CultureInfo.CurrentCulture, ValueFormat, v);
        ResetButton.IsEnabled = !double.IsNaN(DefaultValue) && Math.Abs(v - DefaultValue) > Math.Max(1e-6, Step / 2);
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (!double.IsNaN(DefaultValue)) Value = DefaultValue;
    }
}
