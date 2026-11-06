using System;
using System.Globalization;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.ViewModels;

/// <summary>
/// 倒计时视图模型：预设/自定义分钟，开始/停止，倒计时结束由服务自动提醒。
/// </summary>
public sealed class CountdownViewModel : ObservableObject
{
    private string _timeText = "00:00";
    private string _minutesText = "10";
    private string _statusText = "未开始";
    private string _buttonText = "开始";
    private bool _isRunning;

    /// <summary>构造视图模型并订阅倒计时状态。</summary>
    public CountdownViewModel()
    {
        PresetCommand = new RelayCommand(Preset);
        ToggleCommand = new RelayCommand(Toggle);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        AppServices.Countdown.StateChanged += OnStateChanged;

        if (AppServices.Countdown.IsRunning)
        {
            Refresh();
        }
        else
        {
            MinutesText = Math.Clamp(AppServices.Settings.Current.CountdownMinutes, 1, 600)
                .ToString(CultureInfo.InvariantCulture);
            TimeText = "00:00";
        }
    }

    /// <summary>倒计时显示（mm:ss）。</summary>
    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    /// <summary>设定分钟（文本框双向绑定）。</summary>
    public string MinutesText
    {
        get => _minutesText;
        set => SetProperty(ref _minutesText, value);
    }

    /// <summary>状态文案。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>按钮文案。</summary>
    public string ButtonText
    {
        get => _buttonText;
        private set => SetProperty(ref _buttonText, value);
    }

    /// <summary>是否计时中。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>预设分钟命令（参数为分钟数字符串）。</summary>
    public RelayCommand PresetCommand { get; }

    /// <summary>开始/停止命令。</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>关闭命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>退出订阅（窗口关闭时调用）。</summary>
    public void Detach() => AppServices.Countdown.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Preset(object? parameter)
    {
        if (parameter is null || !int.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
        {
            return;
        }

        MinutesText = Math.Clamp(minutes, 1, 600).ToString(CultureInfo.InvariantCulture);
        AppServices.Countdown.Start(minutes);
        Refresh();
    }

    private void Toggle()
    {
        if (AppServices.Countdown.IsRunning)
        {
            AppServices.Countdown.Stop();
        }
        else
        {
            if (!int.TryParse(MinutesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
            {
                AppServices.Notification.ShowBubble("请输入 1–600 之间的分钟数。", BubbleKind.Info);
                return;
            }

            minutes = Math.Clamp(minutes, 1, 600);
            MinutesText = minutes.ToString(CultureInfo.InvariantCulture);

            AppSettings draft = AppServices.Settings.Current.Clone();
            draft.CountdownMinutes = minutes;
            AppServices.Settings.TryUpdate(draft);

            AppServices.Countdown.Start(minutes);
        }

        Refresh();
    }

    private void Refresh()
    {
        ICountdownService countdown = AppServices.Countdown;
        IsRunning = countdown.IsRunning;

        TimeSpan remaining = countdown.Remaining < TimeSpan.Zero ? TimeSpan.Zero : countdown.Remaining;
        TimeText = string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}",
            (int)remaining.TotalMinutes,
            remaining.Seconds);

        StatusText = countdown.IsRunning
            ? "计时中 · 总共 " + countdown.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟"
            : "未开始";
        ButtonText = countdown.IsRunning ? "停止" : "开始";
    }
}
