using System.Windows;

namespace WorkAlarmClock.Views;

/// <summary>
/// 统计窗口：近 N 天工时柱状图 + 今日时间轴。数据与绑定全部在 StatisticsViewModel，code-behind 仅初始化。
/// </summary>
public partial class StatisticsWindow : Window
{
    /// <summary>构造统计窗口。</summary>
    public StatisticsWindow()
    {
        InitializeComponent();
    }
}
