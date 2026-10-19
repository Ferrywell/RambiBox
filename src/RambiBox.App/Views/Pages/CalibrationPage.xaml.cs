using System.Windows.Controls;
using RambiBox.App.ViewModels;

namespace RambiBox.App.Views.Pages;

public partial class CalibrationPage : UserControl
{
    public CalibrationPage(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => vm.RefreshCalibrationInfo();
    }
}
