using System;
using System.Collections.Generic;
using System.Windows;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.ViewModels;

namespace WorkAlarmClock.Views;

/// <summary>
/// 弹窗管理器：所有工具窗口共用「同一时刻只有一个可见窗体」的规则，
/// 打开新工具时自动隐藏上一个（实例保留，状态不丢），避免桌面堆满窗口。
/// 窗口真正关闭后引用清空，下次打开自动重建。
/// 强提醒遮罩为全屏独占，不参与工具互斥。
/// </summary>
public static class DialogManager
{
    /// <summary>已登记的工具窗口集合，用于互斥显示。</summary>
    private static readonly List<Window> ToolWindows = new();

    private static ReportPreviewWindow? _previewWindow;
    private static StrongReminderWindow? _strongWindow;
    private static StatisticsWindow? _statisticsWindow;
    private static ReportHistoryWindow? _historyWindow;
    private static ClipboardHistoryWindow? _clipboardWindow;
    private static CountdownWindow? _countdownWindow;
    private static WeatherWindow? _weatherWindow;
    private static SpeedTestWindow? _speedTestWindow;
    private static CalculatorWindow? _calculatorWindow;
    private static CalculatorWindow? _radixWindow;
    private static CodeToolWindow? _codeToolWindow;
    private static VariableNameGeneratorWindow? _variableNameWindow;
    private static ImageConverterWindow? _imageConverterWindow;

    public static void ShowVariableNameGenerator()
    {
        if (!Revive(ref _variableNameWindow))
        {
            _variableNameWindow = new VariableNameGeneratorWindow();
            _variableNameWindow.Closed += (_, _) => _variableNameWindow = null;
            Track(_variableNameWindow);
        }
        Present(_variableNameWindow);
    }

    public static void ShowImageConverter()
    {
        if (!Revive(ref _imageConverterWindow))
        {
            _imageConverterWindow = new ImageConverterWindow();
            _imageConverterWindow.Closed += (_, _) => _imageConverterWindow = null;
            Track(_imageConverterWindow);
        }
        Present(_imageConverterWindow);
    }

    /// <summary>显示（或激活）二维码与条码工具。</summary>
    public static void ShowCodeTool()
    {
        if (!Revive(ref _codeToolWindow))
        {
            _codeToolWindow = new CodeToolWindow();
            _codeToolWindow.Closed += (_, _) => _codeToolWindow = null;
            Track(_codeToolWindow);
        }
        Present(_codeToolWindow);
    }

    /// <summary>显示（或激活）计算器窗口。</summary>
    public static void ShowCalculator() => ShowCalculatorWindow("Expression");

    /// <summary>显示（或激活）进制转换窗口。</summary>
    public static void ShowRadixConverter() => ShowCalculatorWindow("Radix");

    private static void ShowCalculatorWindow(string mode)
    {
        if (mode == "Radix")
        {
            if (!Revive(ref _radixWindow))
            {
                _radixWindow = new CalculatorWindow(mode);
                _radixWindow.Closed += (_, _) => _radixWindow = null;
                Track(_radixWindow);
            }
            Present(_radixWindow);
            return;
        }

        if (!Revive(ref _calculatorWindow))
        {
            _calculatorWindow = new CalculatorWindow(mode);
            _calculatorWindow.Closed += (_, _) => _calculatorWindow = null;
            Track(_calculatorWindow);
        }
        Present(_calculatorWindow);
    }

    /// <summary>显示（或激活）网络测速窗口。</summary>
    public static void ShowSpeedTest()
    {
        if (!Revive(ref _speedTestWindow))
        {
            SpeedTestViewModel viewModel = new SpeedTestViewModel();
            _speedTestWindow = new SpeedTestWindow { DataContext = viewModel };
            _speedTestWindow.Closed += (_, _) => _speedTestWindow = null;
            viewModel.CloseRequested += (_, _) => _speedTestWindow?.Close();
            Track(_speedTestWindow);
        }

        Present(_speedTestWindow);
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
            Track(_clipboardWindow);
        }

        Present(_clipboardWindow);
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
            Track(_countdownWindow);
        }

        Present(_countdownWindow);
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
            Track(_weatherWindow);
        }

        Present(_weatherWindow);
    }

    /// <summary>显示（或激活）历史日报窗口。</summary>
    public static void ShowReportHistory()
    {
        if (!Revive(ref _historyWindow))
        {
            ReportHistoryViewModel viewModel = new ReportHistoryViewModel();
            _historyWindow = new ReportHistoryWindow { DataContext = viewModel };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            viewModel.CloseRequested += (_, _) => _historyWindow?.Close();
            Track(_historyWindow);
        }

        Present(_historyWindow);
    }

    /// <summary>显示（或激活）统计窗口。</summary>
    public static void ShowStatistics()
    {
        if (!Revive(ref _statisticsWindow))
        {
            StatisticsViewModel viewModel = new StatisticsViewModel();
            _statisticsWindow = new StatisticsWindow { DataContext = viewModel };
            _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
            viewModel.CloseRequested += (_, _) => _statisticsWindow?.Close();
            Track(_statisticsWindow);
        }

        Present(_statisticsWindow);
    }

    /// <summary>显示（或激活）设置面板（主面板内嵌页，不占独立窗口）。</summary>
    public static void ShowSettings() => App.ShowSettingsPage();

    /// <summary>显示（或激活）日报预览面板。</summary>
    public static void ShowReportPreview()
    {
        if (!Revive(ref _previewWindow))
        {
            ReportPreviewViewModel viewModel = new ReportPreviewViewModel();
            _previewWindow = new ReportPreviewWindow { DataContext = viewModel };
            _previewWindow.Closed += (_, _) => _previewWindow = null;
            viewModel.CloseRequested += (_, _) => _previewWindow?.Close();
            Track(_previewWindow);
        }

        Present(_previewWindow);
    }

    /// <summary>显示（或激活）强提醒全屏遮罩（未确认时不可关闭；全屏独占，不参与工具互斥）。</summary>
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

    /// <summary>登记窗口；真正关闭时自动移出互斥集合。</summary>
    private static void Track(Window window)
    {
        if (!ToolWindows.Contains(window))
        {
            ToolWindows.Add(window);
        }

        window.Closed += (_, _) => ToolWindows.Remove(window);
    }

    /// <summary>显示目标工具窗口，并隐藏其它所有仍可见的工具窗口，保证同一时刻只有一个。</summary>
    private static void Present(Window? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("显示工具窗口失败。", ex);
            return;
        }

        for (int i = ToolWindows.Count - 1; i >= 0; i--)
        {
            Window other = ToolWindows[i];
            if (ReferenceEquals(other, window) || !other.IsVisible)
            {
                continue;
            }

            try
            {
                other.Hide();
            }
            catch (Exception ex)
            {
                AppEnvironment.Log("隐藏其它工具窗口失败。", ex);
            }
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
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
