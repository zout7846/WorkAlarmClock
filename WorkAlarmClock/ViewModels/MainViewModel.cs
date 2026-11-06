using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;
using WorkAlarmClock.Views;

namespace WorkAlarmClock.ViewModels;

/// <summary>
/// 主面板视图模型：状态胶囊、三个状态命令、今日事项 CRUD、日报与统计入口。
/// 托盘菜单与主面板共用这里的命令实例。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    /// <summary>每 N 个节拍刷新一次周统计（读文件较重）。</summary>
    private const int StatsRefreshTicks = 60;

    private string _inputText = string.Empty;
    private string _stateText = "待上班";
    private string _nowText = "--:--:--";
    private string _workDurationText = "00:00";
    private string _restDurationText = "00:00";
    private string _lunchDurationText = "--:--";
    private string _weekWorkText = "00:00";
    private string _weekRestText = "00:00";
    private bool _manualEnded;
    private bool _statsVisible;
    private string _focusTimeText = "25:00";
    private string _focusPhaseText = "未开始";
    private string _focusButtonText = "开始专注";
    private string _focusRoundsText = "今日 0 轮";
    private bool _isFocusRunning;

    private string _weatherTileText = "--";
    private string _weatherTileGlyph = "\uE706";
    private string _weatherTileSub = string.Empty;
    private string _countdownTileText = "--:--";
    private string _clipboardTileText = "0 条";
    private string _networkTileText = "检测中…";
    private string _networkTileSub = string.Empty;
    private System.Windows.Media.Brush _networkTileColor = System.Windows.Media.Brushes.Gray;
    private WorkState _currentState = WorkState.BeforeWork;
    private RestSource? _currentRestSource;
    private int _tickCounter;

    /// <summary>构造主面板视图模型并订阅服务事件。</summary>
    public MainViewModel()
    {
        Tasks = new ObservableCollection<WorkTaskItem>();
        _selectedTag = TagOptions[0];
        TasksView = System.Windows.Data.CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = FilterTask;

        StartWorkCommand = new RelayCommand(
            () => Switch(WorkState.Working),
            () => _currentState != WorkState.Working);
        StartRestCommand = new RelayCommand(
            () => Switch(WorkState.Resting),
            () => _currentState == WorkState.Working || _currentState == WorkState.BeforeWork);
        EndDayCommand = new RelayCommand(
            () => Switch(WorkState.OffWork),
            () => !_manualEnded || _currentState != WorkState.OffWork);
        AddTaskCommand = new RelayCommand(AddTask, () => true);
        RemoveTaskCommand = new RelayCommand(RemoveTask);
        GenerateReportCommand = new RelayCommand(DialogManager.ShowReportPreview);
        OpenPreviewCommand = new RelayCommand(DialogManager.ShowReportPreview);
        OpenSettingsCommand = new RelayCommand(DialogManager.ShowSettings);
        TogglePetCommand = new RelayCommand(() => AppServices.RaisePetToggle());
        ScreenshotCommand = new RelayCommand(() => AppServices.Screenshot.StartCapture());
        OpenStatisticsCommand = new RelayCommand(DialogManager.ShowStatistics);
        OpenHistoryCommand = new RelayCommand(DialogManager.ShowReportHistory);
        OpenClipboardCommand = new RelayCommand(DialogManager.ShowClipboardHistory);
        OpenCountdownCommand = new RelayCommand(DialogManager.ShowCountdown);
        OpenWeatherCommand = new RelayCommand(DialogManager.ShowWeather);
        OpenSpeedTestCommand = new RelayCommand(DialogManager.ShowSpeedTest);
        FocusToggleCommand = new RelayCommand(ToggleFocus);
        FocusSkipCommand = new RelayCommand(() => AppServices.Focus.Skip());
        ToggleStatsCommand = new RelayCommand(() =>
        {
            StatsVisible = !StatsVisible;
            if (StatsVisible)
            {
                RefreshStatistics();
            }
        });
        ExitCommand = new RelayCommand(AppServices.RequestExit);

        AppServices.Ticker.Tick += OnTickerTick;
        AppServices.TimeTracking.StateChanged += OnStateChanged;
        AppServices.TimeTracking.TasksChanged += OnTasksChanged;
        AppServices.Settings.SettingsChanged += OnSettingsChanged;
        AppServices.Focus.StateChanged += OnFocusChanged;
        AppServices.Countdown.StateChanged += OnCountdownChanged;
        AppServices.Weather.Updated += OnWeatherChanged;
        AppServices.ClipboardHistory.Changed += OnClipboardChanged;
        AppServices.Network.StatusChanged += OnNetworkChanged;

        RefreshAll();
        RefreshFocus();
        RefreshCountdown();
        RefreshWeather();
        RefreshClipboard();
        RefreshNetwork();
    }

    /// <summary>今日事项集合。</summary>
    public ObservableCollection<WorkTaskItem> Tasks { get; }

    /// <summary>事项列表视图（按标签过滤）。</summary>
    public System.ComponentModel.ICollectionView TasksView { get; }

    /// <summary>可选标签。</summary>
    public IReadOnlyList<TagOption> TagOptions { get; } = new[]
    {
        new TagOption(string.Empty, "未分类"),
        new TagOption("开发", "开发"),
        new TagOption("会议", "会议"),
        new TagOption("文档", "文档"),
        new TagOption("沟通", "沟通"),
        new TagOption("学习", "学习"),
        new TagOption("工作", "工作"),
        new TagOption("其他", "其他")
    };

    /// <summary>筛选标签项（含「全部」）。</summary>
    public IReadOnlyList<string> TagFilters { get; } = new[] { "全部", "开发", "会议", "文档", "沟通", "学习", "工作", "其他", "未分类" };

    private TagOption _selectedTag;
    private string _selectedTagFilter = "全部";

    /// <summary>新增事项时选中的标签。</summary>
    public TagOption SelectedTag
    {
        get => _selectedTag;
        set => SetProperty(ref _selectedTag, value);
    }

    /// <summary>列表筛选标签（「全部」表示不过滤）。</summary>
    public string SelectedTagFilter
    {
        get => _selectedTagFilter;
        set
        {
            if (SetProperty(ref _selectedTagFilter, value))
            {
                TasksView?.Refresh();
            }
        }
    }

    /// <summary>事项输入框文本。</summary>
    public string InputText
    {
        get => _inputText;
        set => SetProperty(ref _inputText, value);
    }

    /// <summary>状态中文文案。</summary>
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    /// <summary>当前时间文本（HH:mm:ss）。</summary>
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

    /// <summary>今日休息时长文本。</summary>
    public string RestDurationText
    {
        get => _restDurationText;
        private set => SetProperty(ref _restDurationText, value);
    }

    /// <summary>今日午休时长文本（休息中来源为午休的部分）。</summary>
    public string LunchDurationText
    {
        get => _lunchDurationText;
        private set => SetProperty(ref _lunchDurationText, value);
    }

    /// <summary>本周工作时长文本。</summary>
    public string WeekWorkText
    {
        get => _weekWorkText;
        private set => SetProperty(ref _weekWorkText, value);
    }

    /// <summary>本周休息时长文本。</summary>
    public string WeekRestText
    {
        get => _weekRestText;
        private set => SetProperty(ref _weekRestText, value);
    }

    /// <summary>是否已手动结束当天。</summary>
    public bool ManualEnded
    {
        get => _manualEnded;
        private set => SetProperty(ref _manualEnded, value);
    }

    /// <summary>当前状态。</summary>
    public WorkState CurrentState
    {
        get => _currentState;
        private set => SetProperty(ref _currentState, value);
    }

    /// <summary>当前休息来源。</summary>
    public RestSource? CurrentRestSource
    {
        get => _currentRestSource;
        private set => SetProperty(ref _currentRestSource, value);
    }

    /// <summary>统计区块是否可见。</summary>
    public bool StatsVisible
    {
        get => _statsVisible;
        private set => SetProperty(ref _statsVisible, value);
    }

    /// <summary>番茄钟剩余时间（25:00）。</summary>
    public string FocusTimeText
    {
        get => _focusTimeText;
        private set => SetProperty(ref _focusTimeText, value);
    }

    /// <summary>番茄钟阶段文案（专注中 / 短休息 / 长休息 / 未开始）。</summary>
    public string FocusPhaseText
    {
        get => _focusPhaseText;
        private set => SetProperty(ref _focusPhaseText, value);
    }

    /// <summary>番茄钟按钮文案。</summary>
    public string FocusButtonText
    {
        get => _focusButtonText;
        private set => SetProperty(ref _focusButtonText, value);
    }

    /// <summary>今日完成的专注轮数文案。</summary>
    public string FocusRoundsText
    {
        get => _focusRoundsText;
        private set => SetProperty(ref _focusRoundsText, value);
    }

    /// <summary>番茄钟是否正在计时。</summary>
    public bool IsFocusRunning
    {
        get => _isFocusRunning;
        private set => SetProperty(ref _isFocusRunning, value);
    }

    /// <summary>天气磁贴：温度。</summary>
    public string WeatherTileText
    {
        get => _weatherTileText;
        private set => SetProperty(ref _weatherTileText, value);
    }

    /// <summary>天气磁贴：图标。</summary>
    public string WeatherTileGlyph
    {
        get => _weatherTileGlyph;
        private set => SetProperty(ref _weatherTileGlyph, value);
    }

    /// <summary>天气磁贴：城市。</summary>
    public string WeatherTileSub
    {
        get => _weatherTileSub;
        private set => SetProperty(ref _weatherTileSub, value);
    }

    /// <summary>倒计时磁贴文本。</summary>
    public string CountdownTileText
    {
        get => _countdownTileText;
        private set => SetProperty(ref _countdownTileText, value);
    }

    /// <summary>剪贴板磁贴文本。</summary>
    public string ClipboardTileText
    {
        get => _clipboardTileText;
        private set => SetProperty(ref _clipboardTileText, value);
    }

    /// <summary>网络磁贴文本。</summary>
    public string NetworkTileText
    {
        get => _networkTileText;
        private set => SetProperty(ref _networkTileText, value);
    }

    /// <summary>网络磁贴副文本（延迟）。</summary>
    public string NetworkTileSub
    {
        get => _networkTileSub;
        private set => SetProperty(ref _networkTileSub, value);
    }

    /// <summary>网络磁贴状态色。</summary>
    public System.Windows.Media.Brush NetworkTileColor
    {
        get => _networkTileColor;
        private set => SetProperty(ref _networkTileColor, value);
    }

    /// <summary>开始工作命令。</summary>
    public RelayCommand StartWorkCommand { get; }

    /// <summary>开始休息命令。</summary>
    public RelayCommand StartRestCommand { get; }

    /// <summary>结束当天命令。</summary>
    public RelayCommand EndDayCommand { get; }

    /// <summary>添加事项命令。</summary>
    public RelayCommand AddTaskCommand { get; }

    /// <summary>删除事项命令（参数为事项 Id）。</summary>
    public RelayCommand RemoveTaskCommand { get; }

    /// <summary>生成工作汇报命令。</summary>
    public RelayCommand GenerateReportCommand { get; }

    /// <summary>打开日报预览命令。</summary>
    public RelayCommand OpenPreviewCommand { get; }

    /// <summary>打开设置命令。</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>切换桌宠显示/隐藏命令（热键/菜单共用）。</summary>
    public RelayCommand TogglePetCommand { get; }

    /// <summary>区域截图命令。</summary>
    public RelayCommand ScreenshotCommand { get; }

    /// <summary>打开统计窗口命令。</summary>
    public RelayCommand OpenStatisticsCommand { get; }

    /// <summary>打开历史日报命令。</summary>
    public RelayCommand OpenHistoryCommand { get; }

    /// <summary>打开剪贴板历史命令。</summary>
    public RelayCommand OpenClipboardCommand { get; }

    /// <summary>打开倒计时命令。</summary>
    public RelayCommand OpenCountdownCommand { get; }

    /// <summary>打开天气命令。</summary>
    public RelayCommand OpenWeatherCommand { get; }

    /// <summary>打开网络测速命令。</summary>
    public RelayCommand OpenSpeedTestCommand { get; }

    /// <summary>开始/停止番茄钟命令。</summary>
    public RelayCommand FocusToggleCommand { get; }

    /// <summary>跳过当前番茄钟阶段命令。</summary>
    public RelayCommand FocusSkipCommand { get; }

    /// <summary>切换统计区块命令。</summary>
    public RelayCommand ToggleStatsCommand { get; }

    /// <summary>退出程序命令。</summary>
    public RelayCommand ExitCommand { get; }

    private void OnFocusChanged(object? sender, EventArgs e) => RefreshFocus();

    private void ToggleFocus()
    {
        if (AppServices.Focus.IsRunning)
        {
            AppServices.Focus.Stop();
        }
        else
        {
            AppServices.Focus.Start();
        }

        RefreshFocus();
    }

    /// <summary>把番茄钟状态同步到界面。</summary>
    private void RefreshFocus()
    {
        try
        {
            IFocusService focus = AppServices.Focus;
            TimeSpan remaining = focus.Remaining;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            FocusTimeText = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:00}:{1:00}",
                (int)remaining.TotalMinutes,
                remaining.Seconds);

            FocusPhaseText = focus.Phase switch
            {
                FocusPhase.Focus => "专注中",
                FocusPhase.Break => "短休息",
                FocusPhase.LongBreak => "长休息",
                _ => "未开始"
            };

            IsFocusRunning = focus.IsRunning;
            FocusButtonText = focus.IsRunning ? "停止专注" : "开始专注";
            FocusRoundsText = "今日 " + focus.CompletedRounds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 轮";
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("刷新番茄钟状态失败。", ex);
        }
    }

    private void OnCountdownChanged(object? sender, EventArgs e) => RefreshCountdown();

    private void OnWeatherChanged(object? sender, EventArgs e) => RefreshWeather();

    private void OnClipboardChanged(object? sender, EventArgs e) => RefreshClipboard();

    private void OnNetworkChanged(object? sender, EventArgs e) => RefreshNetwork();

    /// <summary>同步倒计时磁贴。</summary>
    private void RefreshCountdown()
    {
        try
        {
            ICountdownService countdown = AppServices.Countdown;
            if (countdown.IsRunning)
            {
                TimeSpan remaining = countdown.Remaining < TimeSpan.Zero ? TimeSpan.Zero : countdown.Remaining;
                CountdownTileText = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:00}:{1:00}",
                    (int)remaining.TotalMinutes,
                    remaining.Seconds);
            }
            else
            {
                CountdownTileText = "--:--";
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("刷新倒计时磁贴失败。", ex);
        }
    }

    /// <summary>同步天气磁贴。</summary>
    private void RefreshWeather()
    {
        try
        {
            WeatherSnapshot? snapshot = AppServices.Weather.Current;
            if (snapshot is null)
            {
                WeatherTileText = "--";
                WeatherTileSub = "点击获取";
                WeatherTileGlyph = "\uE706";
                return;
            }

            WeatherTileText = snapshot.Temperature.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "°";
            WeatherTileSub = snapshot.City;
            WeatherTileGlyph = snapshot.IconGlyph;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("刷新天气磁贴失败。", ex);
        }
    }

    /// <summary>同步剪贴板磁贴。</summary>
    private void RefreshClipboard()
    {
        try
        {
            ClipboardTileText = AppServices.ClipboardHistory.Entries.Count
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + " 条";
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("刷新剪贴板磁贴失败。", ex);
        }
    }

    /// <summary>同步网络磁贴。</summary>
    private void RefreshNetwork()
    {
        try
        {
            INetworkMonitor network = AppServices.Network;
            NetworkTileText = network.IsOnline ? "在线" : "离线";
            NetworkTileSub = network.IsOnline ? network.LatencyMs + "ms" : "无连接";
            NetworkTileColor = network.IsOnline
                ? (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["BrushStateWorking"]
                : System.Windows.Media.Brushes.Silver;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("刷新网络磁贴失败。", ex);
        }
    }

    private void OnTickerTick(object? sender, EventArgs e)
    {
        ITimeTrackingService tracking = AppServices.TimeTracking;
        DateTimeOffset now = AppServices.Ticker.Now;
        NowText = TextFormats.TimeToHms(now);
        WorkDurationText = TextFormats.DurationToHm(tracking.WorkDuration);
        RestDurationText = TextFormats.DurationToHm(tracking.RestDuration);
        LunchDurationText = LunchText(ResolveLunchDuration(now));

        _tickCounter++;
        if (StatsVisible && _tickCounter % StatsRefreshTicks == 0)
        {
            RefreshStatistics();
        }
    }

    private void OnStateChanged(object? sender, WorkState state) => RefreshState();

    private void OnTasksChanged(object? sender, EventArgs e) => ReloadTasks();

    private void OnSettingsChanged(object? sender, EventArgs e) => RefreshState();

    private void RefreshAll()
    {
        RefreshState();
        ReloadTasks();
        RefreshStatistics();
    }

    private void RefreshState()
    {
        ITimeTrackingService tracking = AppServices.TimeTracking;
        DateTimeOffset now = AppServices.Ticker.Now;
        CurrentState = tracking.CurrentState;
        CurrentRestSource = tracking.CurrentRestSource;
        ManualEnded = tracking.ManualEnded;
        StateText = ScheduleService.StateToText(tracking.CurrentState, tracking.CurrentRestSource);
        WorkDurationText = TextFormats.DurationToHm(tracking.WorkDuration);
        RestDurationText = TextFormats.DurationToHm(tracking.RestDuration);
        LunchDurationText = LunchText(ResolveLunchDuration(now));
        NowText = TextFormats.TimeToHms(now);

        StartWorkCommand.RaiseCanExecuteChanged();
        StartRestCommand.RaiseCanExecuteChanged();
        EndDayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>重放流水取午休来源的休息时长（未启用午休返回 null，显示 --:--）。</summary>
    private static TimeSpan? ResolveLunchDuration(DateTimeOffset now)
    {
        AppSettings settings = AppServices.Settings.Current;
        if (!settings.LunchEnabled)
        {
            return null;
        }

        return ReplayCalculator.Replay(AppServices.TimeTracking.Today.Transitions, now, settings).Lunch;
    }

    /// <summary>午休时长文本（未启用显示 --:--）。</summary>
    private static string LunchText(TimeSpan? lunch) =>
        lunch is null ? "--:--" : TextFormats.DurationToHm(lunch.Value);

    private void ReloadTasks()
    {
        List<WorkTaskItem> ordered = AppServices.TimeTracking.Today.Tasks
            .OrderBy(t => t.Timestamp)
            .ToList();

        Tasks.Clear();
        foreach (WorkTaskItem item in ordered)
        {
            Tasks.Add(item);
        }
    }

    private void RefreshStatistics()
    {
        WeekStats stats = AppServices.Statistics.GetStats();
        WeekWorkText = TextFormats.DurationToHm(stats.WeekWork);
        WeekRestText = TextFormats.DurationToHm(stats.WeekRest);
    }

    private void Switch(WorkState target)
    {
        if (!AppServices.TimeTracking.TryManualSwitch(target, out string? error))
        {
            if (!string.IsNullOrEmpty(error))
            {
                AppServices.Notification.ShowBubble(error, BubbleKind.Info);
            }

            return;
        }

        AppServices.Notification.ShowBubble(
            string.Concat("已切换：", ScheduleService.StateToText(target, null)),
            BubbleKind.Info);
        RefreshState();
    }

    private bool FilterTask(object item)
    {
        if (item is not WorkTaskItem task)
        {
            return false;
        }

        string filter = _selectedTagFilter ?? "全部";
        if (filter == "全部")
        {
            return true;
        }

        if (filter == "未分类")
        {
            return string.IsNullOrWhiteSpace(task.Tag);
        }

        return string.Equals(task.Tag, filter, StringComparison.Ordinal);
    }

    private void AddTask()
    {
        string text = (InputText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            AppServices.Notification.ShowBubble("请先输入事项内容。", BubbleKind.Info);
            return;
        }

        AppServices.TimeTracking.AddTask(text, SelectedTag?.Value ?? string.Empty);
        InputText = string.Empty;
        ReloadTasks();
        AppServices.Notification.ShowBubble("已添加事项：" + text, BubbleKind.Info);
    }

    private void RemoveTask(object? parameter)
    {
        Guid id;
        if (parameter is Guid guid)
        {
            id = guid;
        }
        else if (parameter is string text && Guid.TryParse(text, out Guid parsed))
        {
            id = parsed;
        }
        else
        {
            return;
        }

        if (AppServices.TimeTracking.RemoveTask(id))
        {
            ReloadTasks();
            AppServices.Notification.ShowBubble("已删除事项。", BubbleKind.Info);
        }
    }
}

/// <summary>事项标签选项。</summary>
/// <param name="Value">写入事项的标签值（空串表示未分类）。</param>
/// <param name="Text">显示文案。</param>
public sealed record TagOption(string Value, string Text)
{
    /// <summary>下拉框未套用模板时也能显示正确文案。</summary>
    /// <returns>显示文案。</returns>
    public override string ToString() => Text;
}
