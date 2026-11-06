using System.Windows;

namespace WorkAlarmClock.Views;

/// <summary>
/// 天气窗口。
/// </summary>
public partial class WeatherWindow : Window
{
    /// <summary>构造天气窗口。</summary>
    public WeatherWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as ViewModels.WeatherViewModel)?.Detach();
    }
}
