using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RambiBox.App.ViewModels;
using RambiBox.Core;
using RambiBox.Core.Config;

namespace RambiBox.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    private readonly MainViewModel _vm;

    public SettingsPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            vm.PropertyChanged += OnVmChanged;
            UpdateAutostartText();
            bool installed = await Task.Run(AmbiBoxImporter.IsInstalled);
            ImportButton.IsEnabled = installed;
            if (!installed) ImportCard.Description = "AmbiBox settings were not found on this PC.";
            _ = vm.Host.RefreshAmbiBoxAsync();
        };
        Unloaded += (_, _) => vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.AmbiBoxInAutostart) or nameof(MainViewModel.AutostartJustRemoved) or "")
            UpdateAutostartText();
    }

    private void UpdateAutostartText() =>
        AutostartText.Text = _vm.AmbiBoxInAutostart ? "Starts with Windows" : "Not in startup ✓";

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        ImportButton.IsEnabled = false;
        try
        {
            string summary = "";
            var imported = await Task.Run(() => AmbiBoxImporter.TryImport(out summary));
            if (imported is null) return;
            KeepLocalPreferences(imported);
            _vm.Host.ReplaceSettings(imported);
            _vm.ShowImportSummary(summary);
            Log.Info("Settings re-imported from AmbiBox");
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    /// <summary>Settings AmbiBox doesn't know about are kept as they are.</summary>
    private void KeepLocalPreferences(AppSettings s)
    {
        var cur = _vm.Host.Settings;
        s.General = cur.General;
        s.Api = cur.Api;
        s.Capture.BlackBarDetection = cur.Capture.BlackBarDetection;
        s.Capture.MonitorId = cur.Capture.MonitorId;
        s.Capture.Method = cur.Capture.Method;
        s.Static.MoodEffect = cur.Static.MoodEffect;
        s.Static.MoodSpeed = cur.Static.MoodSpeed;
        s.Device.LastPort = cur.Device.LastPort ?? s.Device.LastPort;
        s.Device.LastUsbId = cur.Device.LastUsbId;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ResetConfirm.Visibility = Visibility.Visible;
        ResetButton.IsEnabled = false;
    }

    private void OnResetCancelled(object sender, RoutedEventArgs e)
    {
        ResetConfirm.Visibility = Visibility.Collapsed;
        ResetButton.IsEnabled = true;
        ResetButton.Focus();
    }

    private async void OnResetConfirmed(object sender, RoutedEventArgs e)
    {
        ResetConfirm.Visibility = Visibility.Collapsed;
        var fresh = await Task.Run(() => AmbiBoxImporter.TryImport(out _)) ?? new AppSettings();
        if (fresh.Layout.Zones.Count == 0) fresh.Layout.Zones = LayoutGenerator.Generate(fresh.Layout);
        // keep what the app learned about the hardware
        fresh.Device.LastPort = _vm.Host.Settings.Device.LastPort;
        fresh.Device.LastUsbId = _vm.Host.Settings.Device.LastUsbId;
        _vm.Host.ReplaceSettings(fresh);
        Log.Info("All settings were reset");
        ResetButton.IsEnabled = true;
    }
}
