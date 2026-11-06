using System.Windows;

namespace WorkAlarmClock.Views;

/// <summary>
/// 日报预览窗口：左侧渲染预览，右侧编辑明日计划/风险并重新生成落盘。
/// </summary>
public partial class ReportPreviewWindow : Window
{
    /// <summary>构造日报预览窗口。</summary>
    public ReportPreviewWindow()
    {
        InitializeComponent();
    }
}
