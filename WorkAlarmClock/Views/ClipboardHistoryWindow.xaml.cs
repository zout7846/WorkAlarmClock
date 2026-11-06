using System.Windows;

namespace WorkAlarmClock.Views;

/// <summary>
/// 剪贴板历史窗口。
/// </summary>
public partial class ClipboardHistoryWindow : Window
{
    /// <summary>构造剪贴板历史窗口。</summary>
    public ClipboardHistoryWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as ViewModels.ClipboardHistoryViewModel)?.Detach();
    }
}
