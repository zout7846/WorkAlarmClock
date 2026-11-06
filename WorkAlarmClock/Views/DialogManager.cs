using System;
using System.Windows;
using WorkAlarmClock.ViewModels;

namespace WorkAlarmClock.Views;

/// <summary>
/// 弹窗管理器：设置 / 日报预览 / 强提醒遮罩三个窗口的单例创建与复用（已关闭则重建）。
/// </summary>
public static class DialogManager
{
    private static SettingsWindow? _settingsWindow;
    private static ReportPreviewWindow? _previewWindow;
    private static StrongReminderWindow? _strongWindow;
    private static StatisticsWindow? _statisticsWindow;
    private static ReportHistoryWindow? _historyWindow;
    private static ClipboardHistoryWindow? _clipboardWindow;
    private static CountdownWindow? _countdownWindow;
    private static WeatherWindow? _weatherWindow;
    private static SpeedTestWindow? _speedTestWindow;

    /// <summary>显示（或激活）网络测速窗口。</summary>
    public static void ShowSpeedTest()
    {
        if (!Revive(ref _speedTestWindow))
        {
            SpeedTestViewModel viewModel = new SpeedTestViewModel();
            _speedTestWindow = new SpeedTestWindow { DataContext = viewModel };
            _speedTestWindow.Closed += (_, _) => _speedTestWindow = null;
            viewModel.CloseRequested += (_, _) => _speedTestWindow?.Close();
            _speedTestWindow.Show();
        }

        Activate(_speedTestWindow);
    }

    /// <summary>显示（或激活）剪贴板历史窗口。</summary>
    public static void ShowClipboardHistory()
    {
        if (!Revive(ref _clipboardWindow))
        {
            ClipboardHistoryViewModel viewModel = new ClipboardHistoryViewModel();
            _clipboardWindow = new ClipboardHistoryWindow { DataContext = viewModel };
            _clipboardWindow.Closed += (_, _) => _clipboardWindow = null;
            viewModel.CloseRequested += (_, _) => _clipboardWindow?.Close();
            _clipboardWindow.Show();
        }

        Activate(_clipboardWindow);
    }

    /// <summary>显示（或激活）倒计时窗口。</summary>
    public static void ShowCountdown()
    {
        if (!Revive(ref _countdownWindow))
        {
            CountdownViewModel viewModel = new CountdownViewModel();
            _countdownWindow = new CountdownWindow { DataContext = viewModel };
            _countdownWindow.Closed += (_, _) => _countdownWindow = null;
            viewModel.CloseRequested += (_, _) => _countdownWindow?.Close();
            _countdownWindow.Show();
        }

        Activate(_countdownWindow);
    }

    /// <summary>显示（或激活）天气窗口。</summary>
    public static void ShowWeather()
    {
        if (!Revive(ref _weatherWindow))
        {
            WeatherViewModel viewModel = new WeatherViewModel();
            _weatherWindow = new WeatherWindow { DataContext = viewModel };
            _weatherWindow.Closed += (_, _) => _weatherWindow = null;
            viewModel.CloseRequested += (_, _) => _weatherWindow?.Close();
            _weatherWindow.Show();
        }

        Activate(_weatherWindow);
    }

    /// <summary>显示（或激活）历史日报窗口（每次打开重建视图模型以读取最新数据）。</summary>
    public static void ShowReportHistory()
    {
        if (!Revive(ref _historyWindow))
        {
            ReportHistoryViewModel viewModel = new ReportHistoryViewModel();
            _historyWindow = new ReportHistoryWindow { DataContext = viewModel };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            viewModel.CloseRequested += (_, _) => _historyWindow?.Close();
            _historyWindow.Show();
        }

        Activate(_historyWindow);
    }

    /// <summary>显示（或激活）统计窗口（每次打开重建视图模型以读取最新数据）。</summary>
    public static void ShowStatistics()
    {
        if (!Revive(ref _statisticsWindow))
        {
            StatisticsViewModel viewModel = new StatisticsViewModel();
            _statisticsWindow = new StatisticsWindow { DataContext = viewModel };
            _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
            viewModel.CloseRequested += (_, _) => _statisticsWindow?.Close();
            _statisticsWindow.Show();
        }

        Activate(_statisticsWindow);
    }

    /// <summary>显示（或激活）设置面板。</summary>
    public static void ShowSettings()
    {
        if (!Revive(ref _settingsWindow))
        {
            SettingsViewModel viewModel = new SettingsViewModel();
            _settingsWindow = new SettingsWindow { DataContext = viewModel };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            viewModel.CloseRequested += (_, _) => _settingsWindow?.Close();
            _settingsWindow.Show();
        }

        Activate(_settingsWindow);
    }

    /// <summary>显示（或激活）日报预览面板（每次打开重建视图模型以读取最新数据）。</summary>
    public static void ShowReportPreview()
    {
        if (!Revive(ref _previewWindow))
        {
            ReportPreviewViewModel viewModel = new ReportPreviewViewModel();
            _previewWindow = new ReportPreviewWindow { DataContext = viewModel };
            _previewWindow.Closed += (_, _) => _previewWindow = null;
            viewModel.CloseRequested += (_, _) => _previewWindow?.Close();
            _previewWindow.Show();
        }

        Activate(_previewWindow);
    }

    /// <summary>显示（或激活）强提醒全屏遮罩（未确认时不可关闭）。</summary>
    public static void ShowStrongReminder()
    {
        if (!Revive(ref _strongWindow))
        {
            StrongReminderViewModel viewModel = new StrongReminderViewModel();
            _strongWindow = new StrongReminderWindow { DataContext = viewModel };
            _strongWindow.Closed += (_, _) => _strongWindow = null;
            viewModel.CloseRequested += (_, _) =>
            {
                viewModel.Stop();
                _strongWindow?.Close();
            };
            _strongWindow.Show();
        }

        Activate(_strongWindow);
    }

    /// <summary>窗口若仍存活则激活置前。</summary>
    private static void Activate(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }

    /// <summary>检查窗口引用是否仍可用；不可用则清空引用返回 false。</summary>
    private static bool Revive<T>(ref T? window) where T : Window
    {
        if (window is null)
        {
            return false;
        }

        if (!window.IsLoaded)
        {
            window = null;
            return false;
        }

        return true;
    }
}
