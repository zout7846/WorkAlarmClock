using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.ViewModels;

/// <summary>
/// 日报预览视图模型：实时预览渲染结果，可编辑明日计划/风险后重新生成落盘。
/// </summary>
public sealed class ReportPreviewViewModel : ObservableObject
{
    private string _todayWork;
    private string _tomorrowPlan;
    private string _risks;
    private string _previewText;
    private string _savePathText;

    /// <summary>构造日报预览视图模型（今日工作/明日计划/风险默认取上次生成值）。</summary>
    public ReportPreviewViewModel()
    {
        ReportRecord? last = AppServices.TimeTracking.Today.Reports.LastOrDefault();
        // 未填写过（"-"）时置空，让渲染层回退到自动记录的事项流水。
        _todayWork = NormalizeLast(last?.TodayWork);
        _tomorrowPlan = last?.TomorrowPlan ?? "-";
        _risks = last?.Risks ?? "-";
        _previewText = string.Empty;
        _savePathText = AppServices.Report.GetReportPath(DateOnly.FromDateTime(AppServices.Ticker.Now.LocalDateTime));

        RegenerateCommand = new RelayCommand(Regenerate);
        CopyCommand = new RelayCommand(Copy);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        RefreshPreview();
    }

    /// <summary>今日工作（可编辑；留空则回退主面板自动记录的事项流水）。</summary>
    public string TodayWork
    {
        get => _todayWork;
        set { SetProperty(ref _todayWork, value); RefreshPreview(); }
    }

    /// <summary>明日计划（可编辑）。</summary>
    public string TomorrowPlan
    {
        get => _tomorrowPlan;
        set { SetProperty(ref _tomorrowPlan, value); RefreshPreview(); }
    }

    /// <summary>风险与待办（可编辑）。</summary>
    public string Risks
    {
        get => _risks;
        set { SetProperty(ref _risks, value); RefreshPreview(); }
    }

    /// <summary>预览文本（编辑即重渲染）。</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    /// <summary>保存路径提示文本。</summary>
    public string SavePathText
    {
        get => _savePathText;
        private set => SetProperty(ref _savePathText, value);
    }

    /// <summary>重新生成并落盘命令。</summary>
    public RelayCommand RegenerateCommand { get; }

    /// <summary>复制到剪贴板命令。</summary>
    public RelayCommand CopyCommand { get; }

    /// <summary>打开汇报目录命令。</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>关闭窗口命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>把上次保存的占位值 "-" 归一化为空串，便于回退自动汇总。</summary>
    private static string NormalizeLast(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "-" ? string.Empty : value.Trim();

    private void RefreshPreview()
    {
        try
        {
            PreviewText = AppServices.Report.RenderText(TodayWork, TomorrowPlan, Risks);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("渲染日报预览失败。", ex);
            PreviewText = "渲染失败：" + ex.Message;
        }
    }

    private void Regenerate()
    {
        try
        {
            ReportRecord record = AppServices.Report.Generate(TodayWork, TomorrowPlan, Risks);
            AppServices.Reminder.OnReportGenerated();
            SavePathText = record.FilePath;
            RefreshPreview();
            AppServices.Notification.ShowBubble(
                "日报已生成并保存：" + Path.GetFileName(record.FilePath),
                BubbleKind.Info,
                "打开目录",
                OpenFolder);
            AppServices.Notification.ShowTrayBalloon("工作日报", "日报已生成：" + record.FilePath);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("重新生成日报失败。", ex);
            AppServices.Notification.ShowBubble("生成日报失败：" + ex.Message, BubbleKind.Info);
        }
    }

    private void Copy()
    {
        bool ok = AppServices.Report.CopyToClipboard(PreviewText);
        AppServices.Notification.ShowBubble(
            ok ? "日报已复制到剪贴板。" : "复制失败，请稍后重试。",
            BubbleKind.Info);
    }

    private void OpenFolder()
    {
        try
        {
            string directory = AppServices.Report.ResolveReportDirectory();
            Directory.CreateDirectory(directory);
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
            process?.Dispose();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("打开汇报目录失败。", ex);
            AppServices.Notification.ShowBubble("打开目录失败：" + ex.Message, BubbleKind.Info);
        }
    }
}

/// <summary>
/// 强提醒视图模型：打卡倒计时、确认/忽略与重复提醒（间隔可配置，达上限停止发声但窗口仍不可关）。
/// </summary>
public sealed class StrongReminderViewModel : ObservableObject
{
    private readonly int _repeatIntervalSeconds;
    private readonly int _repeatMaxCount;
    private string _nowText;
    private string _workDurationText;
    private string _reportStatusText;
    private string _countdownText;
    private bool _allowClose;
    private bool _confirmed;
    private int _repeatCount;
    private DateTimeOffset _lastRepeatAt;

    /// <summary>构造强提醒视图模型并开始计时。</summary>
    public StrongReminderViewModel()
    {
        AppSettings settings = AppServices.Settings.Current;
        _repeatIntervalSeconds = Math.Max(1, settings.StrongRepeatIntervalSeconds);
        _repeatMaxCount = Math.Max(0, settings.StrongRepeatMaxCount);
        AllowIgnoreVisible = settings.StrongAllowIgnore;

        _nowText = TextFormats.TimeToHms(AppServices.Ticker.Now);
        _workDurationText = TextFormats.DurationToHm(AppServices.TimeTracking.WorkDuration);
        _reportStatusText = AppServices.TimeTracking.Today.Reports.Count > 0 ? "日报 已生成 ✔" : "日报 未生成";
        _countdownText = string.Empty;
        _lastRepeatAt = AppServices.Ticker.Now;

        ConfirmCommand = new RelayCommand(Confirm);
        IgnoreCommand = new RelayCommand(Ignore);

        AppServices.Ticker.Tick += OnTickerTick;
    }

    /// <summary>当前时间文本。</summary>
    public string NowText
    {
        get => _nowText;
        private set => SetProperty(ref _nowText, value);
    }

    /// <summary>今日工作时长文本。</summary>
    public string WorkDurationText
    {
        get => _workDurationText;
        private set => SetProperty(ref _workDurationText, value);
    }

    /// <summary>日报状态文本。</summary>
    public string ReportStatusText
    {
        get => _reportStatusText;
        private set => SetProperty(ref _reportStatusText, value);
    }

    /// <summary>距下班倒计时文本。</summary>
    public string CountdownText
    {
        get => _countdownText;
        private set => SetProperty(ref _countdownText, value);
    }

    /// <summary>是否允许关闭（已打卡确认或允许忽略后置 true）。</summary>
    public bool AllowClose
    {
        get => _allowClose;
        private set => SetProperty(ref _allowClose, value);
    }

    /// <summary>是否显示"忽略"按钮（仅 StrongAllowIgnore=true）。</summary>
    public bool AllowIgnoreVisible { get; }

    /// <summary>已打卡确认命令。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>忽略命令（仅设置允许时可见）。</summary>
    public RelayCommand IgnoreCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>重复提醒触发事件（窗口闪烁动画 + 提示音）。</summary>
    public event EventHandler? RepeatAlert;

    /// <summary>停止计时（窗口关闭时调用，防事件泄漏）。</summary>
    public void Stop() => AppServices.Ticker.Tick -= OnTickerTick;

    private void Confirm()
    {
        AppServices.Reminder.AcknowledgeStrong();
        _confirmed = true;
        AllowClose = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Ignore()
    {
        // 忽略同样标记当日不再弹（但不视为打卡确认）。
        AppServices.Reminder.AcknowledgeStrong();
        AllowClose = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTickerTick(object? sender, EventArgs e)
    {
        DateTimeOffset now = AppServices.Ticker.Now;
        NowText = TextFormats.TimeToHms(now);
        WorkDurationText = TextFormats.DurationToHm(AppServices.TimeTracking.WorkDuration);
        ReportStatusText = AppServices.TimeTracking.Today.Reports.Count > 0 ? "日报 已生成 ✔" : "日报 未生成";
        UpdateCountdown(now);

        if (_confirmed || _repeatCount >= _repeatMaxCount)
        {
            return;
        }

        if (now - _lastRepeatAt >= TimeSpan.FromSeconds(_repeatIntervalSeconds))
        {
            _lastRepeatAt = now;
            _repeatCount++;
            AppServices.Notification.PlaySound(SoundKind.Strong);
            RepeatAlert?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateCountdown(DateTimeOffset now)
    {
        (DateOnly date, TimeOnly time) = ScheduleService.Split(now);
        TimeOnly workEnd = AppServices.Settings.Current.WorkEndTime;
        if (time < workEnd)
        {
            TimeSpan left = workEnd - time;
            CountdownText = string.Format(CultureInfo.InvariantCulture, "距下班还有 {0} 分钟", (int)Math.Ceiling(left.TotalMinutes));
        }
        else
        {
            CountdownText = "已到下班时间";
        }
    }
}
