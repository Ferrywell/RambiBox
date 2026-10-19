using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RambiBox.App.ViewModels;
using RambiBox.Core.Config;

namespace RambiBox.App.Views.Pages;

public partial class ColorsPage : UserControl
{
    private readonly MainViewModel _vm;

    public ColorsPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        var d = SettingsViewModel.ColorDefaults;
        BrightnessSlider.DefaultValue = d.Brightness * 100;
        SaturationSlider.DefaultValue = d.Saturation * 100;
        GammaSlider.DefaultValue = d.Gamma;
        HueSlider.DefaultValue = d.HueShift;
        HueSlider.Formatter = v => v == 0 ? "0°" : v.ToString("+0;-0", CultureInfo.CurrentCulture) + "°";
        SmoothingSlider.DefaultValue = d.SmoothingMs;
        SmoothingSlider.Formatter = v => v == 0 ? "Off" : $"{v:0} ms";
        DarkSlider.DefaultValue = Math.Round(d.DarkThreshold * 100, 1);
        MinSlider.DefaultValue = d.MinBrightness * 100;
        RedSlider.DefaultValue = d.WhiteR * 100;
        GreenSlider.DefaultValue = d.WhiteG * 100;
        BlueSlider.DefaultValue = d.WhiteB * 100;

        ResetImported.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            bool installed = await Task.Run(AmbiBoxImporter.IsInstalled);
            ResetImported.IsEnabled = installed;
            if (!installed) ResetImported.ToolTip = "AmbiBox settings were not found on this PC.";
        };
    }

    private async void OnResetImported(object sender, RoutedEventArgs e)
    {
        var imported = await Task.Run(() => AmbiBoxImporter.TryImport(out _));
        if (imported is null) return;
        // keep the calibration's per-channel correction; AmbiBox doesn't have it
        var c = imported.Color;
        var cur = _vm.Host.Settings.Color;
        c.ChannelGammaR = cur.ChannelGammaR;
        c.ChannelGammaG = cur.ChannelGammaG;
        c.ChannelGammaB = cur.ChannelGammaB;
        _vm.S.ApplyColorSettings(c);
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e) => _vm.S.ApplyColorSettings(new ColorSettings());
}
