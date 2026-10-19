using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RambiBox.App.ViewModels;
using RambiBox.Core.Config;

namespace RambiBox.App.Views.Pages;

public partial class StaticMoodPage : UserControl
{
    private readonly MainViewModel _vm;

    public StaticMoodPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SpeedSlider.Formatter = v => v.ToString("0.0", CultureInfo.CurrentCulture) + "×";
        Loaded += (_, _) =>
        {
            vm.S.PropertyChanged += OnSettingsChanged;
            UpdateBreathe();
        };
        Unloaded += (_, _) => vm.S.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.StaticColor)) UpdateBreathe();
    }

    private void UpdateBreathe()
    {
        var c = _vm.S.StaticColor;
        BreatheSwatch.Background = new LinearGradientBrush(Color.FromArgb(0x40, c.R, c.G, c.B), c, 0);
    }

    private void OnUseStatic(object sender, RoutedEventArgs e)
    {
        _vm.S.Mode = LightMode.StaticColor;
        _vm.S.Enabled = true;
    }

    private void OnUseMood(object sender, RoutedEventArgs e)
    {
        _vm.S.Mode = LightMode.Mood;
        _vm.S.Enabled = true;
    }
}
