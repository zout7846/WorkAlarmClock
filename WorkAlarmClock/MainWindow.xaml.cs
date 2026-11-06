using System;
using System.ComponentModel;
using System.Windows;

namespace WorkAlarmClock
{
    /// <summary>
    /// 主面板窗口：状态胶囊、状态操作、今日事项与日报/统计/设置入口。
    /// 关闭窗口时拦截并隐藏，程序继续驻留系统托盘。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>构造主面板窗口。</summary>
        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>关闭时隐藏到托盘（真正的退出走托盘菜单「退出」）。</summary>
        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
