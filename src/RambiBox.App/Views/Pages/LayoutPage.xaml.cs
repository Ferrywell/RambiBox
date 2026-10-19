using System.Windows;
using System.Windows.Controls;
using RambiBox.App.ViewModels;
using RambiBox.Core.Config;

namespace RambiBox.App.Views.Pages;

public partial class LayoutPage : UserControl
{
    private readonly MainViewModel _vm;

    public LayoutPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        var defaults = new LayoutSettings();
        DepthH.DefaultValue = Math.Round(defaults.DepthHorizontal * 100, 1);
        DepthV.DefaultValue = Math.Round(defaults.DepthVertical * 100, 1);
        var driver = new PreviewDriver(this, Preview, vm);
        void Redraw() => driver.Tick();
        Loaded += async (_, _) =>
        {
            vm.S.LayoutChanged += Redraw;
            bool installed = await Task.Run(AmbiBoxImporter.IsInstalled);
            ImportButton.IsEnabled = installed;
            if (!installed) ImportNote.Text = "AmbiBox settings were not found on this PC.";
        };
        Unloaded += (_, _) => vm.S.LayoutChanged -= Redraw;
    }

    private void OnUnlock(object sender, RoutedEventArgs e) => _vm.S.LayoutUnlocked = true;

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        ImportButton.IsEnabled = false;
        try
        {
            var imported = await Task.Run(() => AmbiBoxImporter.TryImport(out _));
            if (imported is null)
            {
                ImportNote.Text = "Could not read the AmbiBox layout.";
                return;
            }
            _vm.S.ReplaceLayout(imported.Layout);
            ImportNote.Text = $"Imported {imported.Layout.Zones.Count} LEDs from AmbiBox.";
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }
}
