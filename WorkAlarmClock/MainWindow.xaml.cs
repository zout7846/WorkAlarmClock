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
            Loaded += (_, _) => Views.WorkspaceFrame.FitToWorkArea(this);
        }

        private ViewModels.SettingsViewModel? _settingsViewModel;

        public void ShowSettingsPage() => WorkspaceTabs.SelectedIndex = 4;
        public void ShowToolsPage() => WorkspaceTabs.SelectedIndex = 2;

        private void OnPageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, WorkspaceTabs) || SettingsHost is null) return;
            if (WorkspaceTabs.SelectedIndex == 4 && SettingsHost.Content is null)
            {
                _settingsViewModel = new ViewModels.SettingsViewModel();
                _settingsViewModel.CloseRequested += OnSettingsFinished;
                SettingsHost.Content = new Views.SettingsPage { DataContext = _settingsViewModel };
            }
        }

        private void OnSettingsFinished(object? sender, EventArgs e)
        {
            if (_settingsViewModel is not null) _settingsViewModel.CloseRequested -= OnSettingsFinished;
            _settingsViewModel = null;
            SettingsHost.Content = null;
            WorkspaceTabs.SelectedIndex = 0;
        }

        private void OpenTools(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 2;

        private void OpenTasks(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 1;

        /// <summary>关闭时隐藏到托盘（真正的退出走托盘菜单「退出」）。</summary>
        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
