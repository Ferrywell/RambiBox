using System.Windows.Controls;
using System.Windows.Threading;
using RambiBox.App.ViewModels;

namespace RambiBox.App.Views.Pages;

public partial class DevicePage : UserControl
{
    private const int MaxLines = 50;
    private readonly MainViewModel _vm;
    private int _pending; // 1 while a UI refresh is queued (coalesces bursts of log lines)

    public DevicePage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) =>
        {
            vm.Host.Logs.LineAdded += OnLine;
            RefreshLog();
        };
        Unloaded += (_, _) => vm.Host.Logs.LineAdded -= OnLine;
    }

    private void OnLine(string line)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshLog);
    }

    private void RefreshLog()
    {
        Interlocked.Exchange(ref _pending, 0);
        bool atEnd = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 2;
        LogBox.Text = string.Join(Environment.NewLine, _vm.Host.Logs.Snapshot(MaxLines));
        if (atEnd || LogBox.ExtentHeight <= 0) LogBox.ScrollToEnd();
    }
}
