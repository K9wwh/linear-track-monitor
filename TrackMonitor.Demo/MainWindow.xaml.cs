using System;
using System.Windows;

namespace TrackMonitor.Demo;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // DataContext(AxisMonitorViewModel)现在在 XAML 里声明,并自建持有传感器。
        // 关窗时把它 Dispose,停掉后台定时器,避免继续往已关闭的界面投递。
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
