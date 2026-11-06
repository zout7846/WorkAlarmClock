using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;
using WorkAlarmClock.Views;

namespace WorkAlarmClock.ViewModels;

/// <summary>
/// 桌宠悬浮窗视图模型：时钟、时长、状态、表情叠加与气泡。
/// </summary>
public sealed class PetViewModel : ObservableObject
{
    /// <summary>气泡显示时长（秒）。</summary>
    private const int BubbleSeconds = 8;

    /// <summary>提醒表情叠加显示时长（秒）。</summary>
    private const int OverlaySeconds = 60;

    private string _timeText = "--:--";
    private string _workDurationText = "00:00";
    private string _stateText = "待上班";
    private Brush _stateDotBrush = Brushes.Gray;
    private string _bubbleText = string.Empty;
    private bool _bubbleVisible;
    private string? _bubbleActionLabel;
    private double _petOpacity = 0.8;
    private WorkState _currentState = WorkState.BeforeWork;
    private RestSource? _currentRestSource;
    private ReminderKind? _reminderOverlay;
    private Action? _singleBubbleAction;
    private DateTimeOffset? _bubbleExpireAt;
    private DateTimeOffset? _overlayExpireAt;

    /// <summary>亲密度等级门槛（与称号）。</summary>
    private static readonly (int Min, string Title)[] Levels =
    {
        (0, "初见"),
        (20, "熟悉"),
        (60, "搭档"),
        (150, "老铁"),
        (350, "铁子")
    };

    private static readonly string[][] Greetings =
    {
        new[] { "嗨～", "我在的。", "今天也要加油鸭" },
        new[] { "又见面啦！", "摸摸头～", "需要我帮忙吗？" },
        new[] { "老朋友了，记得喝水～", "一起加油！", "累了就歇会儿" },
        new[] { "你回来啦，我都等半天了", "嘿嘿，今天也很努力呀", "有我在，别慌" },
        new[] { "铁子，别硬撑，歇会儿吧", "陪你加班到底", "你说话我就应" }
    };

    private int _intimacy;
    private int _species;
    private int _skin = -1;
    private Brush _furGradient = Brushes.LightBlue;
    private Brush _furSolid = Brushes.CornflowerBlue;
    private Brush _furLight = Brushes.AliceBlue;
    private Brush _furOutline = Brushes.SteelBlue;
    private Brush _earInner = Brushes.Pink;
    private Brush _blush = Brushes.LightPink;
    private Brush _noseColor = Brushes.PaleVioletRed;
    private string _levelBadge = "Lv.1 初见";
    private DateTimeOffset _lastInteractionAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPersistAt = DateTimeOffset.MinValue;
    private readonly Random _random = new Random();

    /// <summary>构造桌宠视图模型并订阅全部服务事件。</summary>
    public PetViewModel()
    {
        BubbleActions = new ObservableCollection<BubbleAction>();

        OpenMainPanelCommand = new RelayCommand(() => AppServices.Notification.ShowMainPanel());
        BubbleActionCommand = new RelayCommand(ExecuteBubbleAction);
        StartWorkCommand = new RelayCommand(() => ManualSwitch(WorkState.Working));
        StartRestCommand = new RelayCommand(() => ManualSwitch(WorkState.Resting));
        EndDayCommand = new RelayCommand(() => ManualSwitch(WorkState.OffWork));
        GenerateReportCommand = new RelayCommand(DialogManager.ShowReportPreview);
        ScreenshotCommand = new RelayCommand(() => AppServices.Screenshot.StartCapture());
        OpenStatisticsCommand = new RelayCommand(DialogManager.ShowStatistics);
        OpenHistoryCommand = new RelayCommand(DialogManager.ShowReportHistory);
        FocusToggleCommand = new RelayCommand(ToggleFocus);
        OpenSettingsCommand = new RelayCommand(DialogManager.ShowSettings);
        ExitCommand = new RelayCommand(AppServices.RequestExit);

        _intimacy = Math.Max(0, AppServices.Settings.Current.PetIntimacy);
        _species = AppServices.Settings.Current.PetSpecies;
        UpdateLevelBadge();
        ApplySkin(AppServices.Settings.Current.PetSkin);

        AppServices.Ticker.Tick += OnTickerTick;
        AppServices.TimeTracking.StateChanged += OnStateChanged;
        AppServices.Settings.SettingsChanged += OnSettingsChanged;
        AppServices.Reminder.ReminderDue += OnReminderDue;
        AppServices.BubbleRequested += OnBubbleRequested;
        AppServices.LiveOpacityPreview += OnLiveOpacityPreview;
        AppServices.Focus.StateChanged += OnFocusChanged;

        RefreshFromServices();
        RefreshFocusChip();
    }

    /// <summary>当前时间文本（HH:mm）。</summary>
    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    /// <summary>今日已工作时长文本（HH:mm）。</summary>
    public string WorkDurationText
    {
        get => _workDurationText;
        private set => SetProperty(ref _workDurationText, value);
    }

    /// <summary>状态中文文案。</summary>
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    /// <summary>状态圆点颜色。</summary>
    public Brush StateDotBrush
    {
        get => _stateDotBrush;
        private set => SetProperty(ref _stateDotBrush, value);
    }

    /// <summary>气泡文本（空表示无气泡）。</summary>
    public string BubbleText
    {
        get => _bubbleText;
        private set => SetProperty(ref _bubbleText, value);
    }

    /// <summary>气泡是否可见（用于淡入淡出动画）。</summary>
    public bool BubbleVisible
    {
        get => _bubbleVisible;
        private set => SetProperty(ref _bubbleVisible, value);
    }

    /// <summary>单动作气泡的按钮文案。</summary>
    public string? BubbleActionLabel
    {
        get => _bubbleActionLabel;
        private set => SetProperty(ref _bubbleActionLabel, value);
    }

    /// <summary>多动作气泡按钮集合。</summary>
    public ObservableCollection<BubbleAction> BubbleActions { get; }

    /// <summary>桌宠不透明度（来自设置）。</summary>
    public double PetOpacity
    {
        get => _petOpacity;
        private set => SetProperty(ref _petOpacity, value);
    }

    /// <summary>当前主状态（供表情控件绑定）。</summary>
    public WorkState CurrentState
    {
        get => _currentState;
        private set => SetProperty(ref _currentState, value);
    }

    /// <summary>当前休息来源（供表情控件绑定）。</summary>
    public RestSource? CurrentRestSource
    {
        get => _currentRestSource;
        private set => SetProperty(ref _currentRestSource, value);
    }

    /// <summary>提醒表情叠加（弱/强提醒）。</summary>
    public ReminderKind? ReminderOverlay
    {
        get => _reminderOverlay;
        private set => SetProperty(ref _reminderOverlay, value);
    }

    /// <summary>打开主面板命令。</summary>
    public RelayCommand OpenMainPanelCommand { get; }

    /// <summary>气泡动作执行命令。</summary>
    public RelayCommand BubbleActionCommand { get; }

    /// <summary>开始工作命令。</summary>
    public RelayCommand StartWorkCommand { get; }

    /// <summary>开始休息命令。</summary>
    public RelayCommand StartRestCommand { get; }

    /// <summary>结束当天命令。</summary>
    public RelayCommand EndDayCommand { get; }

    /// <summary>生成工作汇报命令。</summary>
    public RelayCommand GenerateReportCommand { get; }

    /// <summary>区域截图命令。</summary>
    public RelayCommand ScreenshotCommand { get; }

    /// <summary>打开统计窗口命令。</summary>
    public RelayCommand OpenStatisticsCommand { get; }

    /// <summary>打开历史日报命令。</summary>
    public RelayCommand OpenHistoryCommand { get; }

    /// <summary>开始/停止番茄钟命令。</summary>
    public RelayCommand FocusToggleCommand { get; }

    /// <summary>亲密度等级徽章（Lv.2 熟悉）。</summary>
    public string LevelBadge
    {
        get => _levelBadge;
        private set => SetProperty(ref _levelBadge, value);
    }

    /// <summary>亲密度数值。</summary>
    public int Intimacy => _intimacy;

    private bool _isFocusChipVisible;
    private string _focusChipText = string.Empty;

    /// <summary>当前皮肤序号（0–3）。</summary>
    public int Skin => _skin;

    /// <summary>桌宠形象：0=小猫 1=小狗 2=狐狸。</summary>
    public int Species
    {
        get => _species;
        private set => SetProperty(ref _species, value);
    }

    /// <summary>是否显示番茄钟倒计时胶囊。</summary>
    public bool IsFocusChipVisible
    {
        get => _isFocusChipVisible;
        private set => SetProperty(ref _isFocusChipVisible, value);
    }

    /// <summary>番茄钟倒计时文案（专注 24:12 / 休息 04:30）。</summary>
    public string FocusChipText
    {
        get => _focusChipText;
        private set => SetProperty(ref _focusChipText, value);
    }

    /// <summary>毛色渐变（头/身体/耳朵）。</summary>
    public Brush FurGradient
    {
        get => _furGradient;
        private set => SetProperty(ref _furGradient, value);
    }

    /// <summary>纯色毛色（尾巴/眨眼遮罩）。</summary>
    public Brush FurSolid
    {
        get => _furSolid;
        private set => SetProperty(ref _furSolid, value);
    }

    /// <summary>浅色毛（脚掌）。</summary>
    public Brush FurLight
    {
        get => _furLight;
        private set => SetProperty(ref _furLight, value);
    }

    /// <summary>描边色。</summary>
    public Brush FurOutline
    {
        get => _furOutline;
        private set => SetProperty(ref _furOutline, value);
    }

    /// <summary>耳内粉色。</summary>
    public Brush EarInner
    {
        get => _earInner;
        private set => SetProperty(ref _earInner, value);
    }

    /// <summary>腮红色。</summary>
    public Brush Blush
    {
        get => _blush;
        private set => SetProperty(ref _blush, value);
    }

    /// <summary>鼻子颜色。</summary>
    public Brush NoseColor
    {
        get => _noseColor;
        private set => SetProperty(ref _noseColor, value);
    }

    /// <summary>应用皮肤配色（0=蓝猫 1=粉兔 2=薄荷 3=暖橘）。</summary>
    /// <param name="skin">皮肤序号。</param>
    private void ApplySkin(int skin)
    {
        int index = ((skin % 4) + 4) % 4;
        if (index == _skin && _furGradient is not null)
        {
            return;
        }

        _skin = index;
        (string from, string to, string solid, string light, string outline, string ear, string blush, string nose) palette = index switch
        {
            1 => ("#FFFF9DBF", "#FFE86A8E", "#FFF08AA8", "#FFFFE3EE", "#FFB85A7A", "#FFFFE3EE", "#FFFF8FB0", "#FFE86A8E"),
            2 => ("#FF7FD8B4", "#FF3FA98A", "#FF5CC29A", "#FFD9F5EA", "#FF2E8A70", "#FFD9F5EA", "#FFFF9FB4", "#FFE58F6E"),
            3 => ("#FFD98A5B", "#FFA85A2A", "#FFC0764A", "#FFF2D9C4", "#FF8A4A22", "#FFF2D9C4", "#FFFF9B8A", "#FFE07A5F"),
            _ => ("#FF8FB8FF", "#FF4A6FD8", "#FF6A8FE0", "#FFD6E6FF", "#FF3A5BB8", "#FFD6E6FF", "#FFFF9FB4", "#FFF08AA8")
        };

        FurGradient = CreateGradient(palette.from, palette.to);
        FurSolid = CreateSolid(palette.solid);
        FurLight = CreateSolid(palette.light);
        FurOutline = CreateSolid(palette.outline);
        EarInner = CreateSolid(palette.ear);
        Blush = CreateSolid(palette.blush);
        NoseColor = CreateSolid(palette.nose);
        OnPropertyChanged(nameof(Skin));
    }

    private static Brush CreateSolid(string hex)
    {
        SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateGradient(string from, string to)
    {
        LinearGradientBrush brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(from),
            (Color)ColorConverter.ConvertFromString(to),
            new System.Windows.Point(0.25, 0),
            new System.Windows.Point(0.75, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 记录一次点击互动：提升亲密度并返回一句台词（3 秒内重复点击不重复计数）。
    /// </summary>
    /// <returns>随机台词。</returns>
    public string RegisterInteraction()
    {
        DateTimeOffset now = AppServices.Ticker.Now;
        string greeting = PickGreeting(now);

        if (now - _lastInteractionAt < TimeSpan.FromSeconds(3))
        {
            return greeting;
        }

        _lastInteractionAt = now;
        int beforeLevel = LevelIndex(_intimacy);
        _intimacy = Math.Min(9999, _intimacy + 1);

        if (LevelIndex(_intimacy) != beforeLevel)
        {
            UpdateLevelBadge();
            greeting = "升级啦！现在是 " + _levelBadge + "，谢谢你一直陪着我～";
            PersistIntimacy();
        }
        else if (now - _lastPersistAt > TimeSpan.FromSeconds(60))
        {
            PersistIntimacy();
        }

        return greeting;
    }

    /// <summary>把亲密度写回设置（窗口关闭时调用，保证不丢）。</summary>
    public void PersistIntimacy()
    {
        try
        {
            if (AppServices.Settings.Current.PetIntimacy == _intimacy)
            {
                return;
            }

            AppSettings draft = AppServices.Settings.Current.Clone();
            draft.PetIntimacy = _intimacy;
            AppServices.Settings.TryUpdate(draft);
            _lastPersistAt = AppServices.Ticker.Now;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("保存亲密度失败。", ex);
        }
    }

    private int LevelIndex(int intimacy)
    {
        int index = 0;
        for (int i = 0; i < Levels.Length; i++)
        {
            if (intimacy >= Levels[i].Min)
            {
                index = i;
            }
        }

        return index;
    }

    private void UpdateLevelBadge()
    {
        int index = LevelIndex(_intimacy);
        LevelBadge = "Lv." + (index + 1).ToString(CultureInfo.InvariantCulture) + " " + Levels[index].Title;
    }

    private string PickGreeting(DateTimeOffset now)
    {
        int index = Math.Clamp(LevelIndex(_intimacy), 0, Greetings.Length - 1);
        string[] pool = Greetings[index];
        return pool[_random.Next(pool.Length)];
    }

    /// <summary>打开设置命令。</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>退出程序命令。</summary>
    public RelayCommand ExitCommand { get; }

    private void OnTickerTick(object? sender, EventArgs e)
    {
        DateTimeOffset now = AppServices.Ticker.Now;
        TimeText = TextFormats.TimeToHm(now);
        WorkDurationText = TextFormats.DurationToHm(AppServices.TimeTracking.WorkDuration);

        if (_bubbleExpireAt.HasValue && now >= _bubbleExpireAt.Value)
        {
            ClearBubble();
        }

        if (_overlayExpireAt.HasValue && now >= _overlayExpireAt.Value)
        {
            ReminderOverlay = null;
            _overlayExpireAt = null;
        }
    }

    private void OnStateChanged(object? sender, WorkState state) => RefreshFromServices();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        RefreshFromServices();
        ApplySkin(AppServices.Settings.Current.PetSkin);
        Species = AppServices.Settings.Current.PetSpecies;
    }

    private void OnFocusChanged(object? sender, EventArgs e) => RefreshFocusChip();

    /// <summary>开始/停止番茄钟。</summary>
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

        RefreshFocusChip();
    }

    /// <summary>同步番茄钟倒计时胶囊（未在计时则隐藏）。</summary>
    private void RefreshFocusChip()
    {
        try
        {
            IFocusService focus = AppServices.Focus;
            IsFocusChipVisible = focus.IsRunning && focus.Remaining > TimeSpan.Zero;

            TimeSpan remaining = focus.Remaining < TimeSpan.Zero ? TimeSpan.Zero : focus.Remaining;
            string mmss = string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}",
                (int)remaining.TotalMinutes,
                remaining.Seconds);

            FocusChipText = focus.Phase switch
            {
                FocusPhase.Focus => "专注 " + mmss,
                FocusPhase.Break => "休息 " + mmss,
                FocusPhase.LongBreak => "长休息 " + mmss,
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("刷新番茄钟胶囊失败。", ex);
        }
    }

    /// <summary>设置面板拖动透明度滑条时的实时预览（保存/取消前不落盘）。</summary>
    private void OnLiveOpacityPreview(object? sender, double opacity) => PetOpacity = opacity;

    private void OnReminderDue(object? sender, ReminderKind kind)
    {
        ReminderOverlay = kind;
        _overlayExpireAt = AppServices.Ticker.Now.AddSeconds(OverlaySeconds);
    }

    private void OnBubbleRequested(object? sender, BubbleRequest request)
    {
        _singleBubbleAction = request.Action;
        BubbleActionLabel = request.Actions.Count > 0 ? null : request.ActionLabel;
        BubbleActions.Clear();
        foreach (BubbleAction action in request.Actions)
        {
            BubbleActions.Add(action);
        }

        BubbleText = request.Message;
        BubbleVisible = true;
        _bubbleExpireAt = AppServices.Ticker.Now.AddSeconds(BubbleSeconds);
    }

    private void ExecuteBubbleAction(object? parameter)
    {
        if (parameter is BubbleAction bubbleAction)
        {
            bubbleAction.Action();
        }
        else
        {
            _singleBubbleAction?.Invoke();
        }

        ClearBubble();
    }

    private void ClearBubble()
    {
        BubbleVisible = false;
        BubbleText = string.Empty;
        BubbleActionLabel = null;
        BubbleActions.Clear();
        _singleBubbleAction = null;
        _bubbleExpireAt = null;
    }

    private void ManualSwitch(WorkState target)
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
    }

    private void RefreshFromServices()
    {
        ITimeTrackingService tracking = AppServices.TimeTracking;
        CurrentState = tracking.CurrentState;
        CurrentRestSource = tracking.CurrentRestSource;
        StateText = ScheduleService.StateToText(tracking.CurrentState, tracking.CurrentRestSource);
        StateDotBrush = ResolveBrush(tracking.CurrentState, tracking.CurrentRestSource);
        WorkDurationText = TextFormats.DurationToHm(tracking.WorkDuration);
        TimeText = TextFormats.TimeToHm(AppServices.Ticker.Now);
        PetOpacity = AppServices.Settings.Current.PetOpacity;
    }

    private static Brush ResolveBrush(WorkState state, RestSource? restSource)
    {
        return state switch
        {
            WorkState.Working => Brushes.MediumSeaGreen,
            WorkState.Resting => restSource == RestSource.Lunch ? Brushes.Orange : Brushes.Goldenrod,
            WorkState.OffWork => Brushes.Gray,
            _ => Brushes.SlateGray
        };
    }
}
