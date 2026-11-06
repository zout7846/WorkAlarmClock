using System.Windows;

namespace WorkAlarmClock.Views;

/// <summary>
/// 倒计时窗口。
/// </summary>
public partial class CountdownWindow : Window
{
    /// <summary>构造倒计时窗口。</summary>
    public CountdownWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as ViewModels.CountdownViewModel)?.Detach();
    }
}
