using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using RambiBox.App.ViewModels;

namespace RambiBox.App.Views.Pages;

public partial class HomePage : UserControl
{
    private readonly MainViewModel _vm;

    public HomePage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _ = new PreviewDriver(this, Preview, vm);
        Loaded += (_, _) =>
        {
            vm.S.PropertyChanged += OnSettingsPropertyChanged;
            UpdateSwatch();
        };
        Unloaded += (_, _) => vm.S.PropertyChanged -= OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsViewModel.StaticColor)) UpdateSwatch();
    }

    private void UpdateSwatch() => StaticSwatch.Background = new SolidColorBrush(_vm.S.StaticColor);
}
