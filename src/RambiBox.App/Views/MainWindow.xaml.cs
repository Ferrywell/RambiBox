using System.Windows;
using RambiBox.App.ViewModels;
using RambiBox.App.Views.Pages;

namespace RambiBox.App.Views;

/// <summary>
/// The settings window. Created on demand and fully released on close; pages are created when shown
/// and dropped when you navigate away, so nothing UI-related runs while the app sits in the tray.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(AppHost host, string? page)
    {
        InitializeComponent();
        _vm = new MainViewModel(host);
        DataContext = _vm;
        _vm.NavigationChanged += Navigate;

        var start = _vm.NavItems.FirstOrDefault(n => n.Key == page);
        if (start is not null && start != _vm.SelectedNav) _vm.SelectedNav = start;
        else Navigate(_vm.SelectedNav.Key);

        IsVisibleChanged += (_, _) => UpdateActive();
        StateChanged += (_, _) => UpdateActive();
        Closed += OnClosed;
    }

    private void Navigate(string key)
    {
        FrameworkElement page = key switch
        {
            "colors" => new ColorsPage(_vm),
            "static" => new StaticMoodPage(_vm),
            "layout" => new LayoutPage(_vm),
            "device" => new DevicePage(_vm),
            "calibration" => new CalibrationPage(_vm),
            "settings" => new SettingsPage(_vm),
            _ => new HomePage(_vm),
        };
        PageHost.Content = page;
        Scroller.ScrollToTop();
    }

    private void UpdateActive() => _vm.IsViewActive = IsVisible && WindowState != WindowState.Minimized;

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.NavigationChanged -= Navigate;
        PageHost.Content = null; // unloads the page (stops its preview timer)
        _vm.IsViewActive = false;
        _vm.Dispose();
        DataContext = null;
    }
}
