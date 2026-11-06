using System.Windows;

namespace WorkAlarmClock.Views;

/// <summary>
/// 历史日报窗口：左侧按日期列出已生成日报，右侧预览内容，支持搜索与一键生成周报。
/// </summary>
public partial class ReportHistoryWindow : Window
{
    /// <summary>构造历史日报窗口。</summary>
    public ReportHistoryWindow()
    {
        InitializeComponent();
    }
}
