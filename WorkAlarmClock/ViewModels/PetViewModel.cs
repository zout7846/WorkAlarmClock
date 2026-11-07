using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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

    /// <summary>活动结束后灵动岛保留文案的时长（秒），可被新活动打断。</summary>
    private const int ActivityHoldSeconds = 3;

    private string _timeText = "--:--";
    private string _workDurationText = "00:00";
    private string _stateText = "待上班";
    private Brush _stateDotBrush = Brushes.Gray;
    private Brush _stateGlowBrush = Brushes.Transparent;
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

    private string _islandTaskText = "今日 0 条";

    private bool _isCameraInUse;
    private bool _isMicInUse;
    private bool _isMediaPlaying;
    private bool _isCapturing;

    private string _islandActivityText = string.Empty;
    private Brush _islandActivityBrush = Brushes.LightSteelBlue;
    private bool _islandActivityVisible;
    private readonly DispatcherTimer _activityHoldTimer;

    private DateTimeOffset _lastInteractionAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPersistAt = DateTimeOffset.MinValue;
    private readonly Random _random = new Random();

    /// <summary>构造桌宠视图模型并订阅全部服务事件。</summary>
    public PetViewModel()
    {
        BubbleActions = new ObservableCollection<BubbleAction>();
        _activityHoldTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(ActivityHoldSeconds)
        };
        _activityHoldTimer.Tick += (_, _) => ClearIslandActivity();

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
        AppServices.SystemStatus.StatusChanged += OnSystemStatusChanged;
        RefreshSystemStatus();

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

    /// <summary>状态文字背后的柔和光晕（径向渐变，颜色跟随状态色）。</summary>
    public Brush StateGlowBrush
    {
        get => _stateGlowBrush;
        private set => SetProperty(ref _stateGlowBrush, value);
    }

    /// <summary>灵动岛活动文案（"微信 · 摄像头"），无活动时为空。</summary>
    public string IslandActivityText
    {
        get => _islandActivityText;
        private set => SetProperty(ref _islandActivityText, value);
    }

    /// <summary>灵动岛活动文案颜色（按活动类型着色）。</summary>
    public Brush IslandActivityBrush
    {
        get => _islandActivityBrush;
        private set => SetProperty(ref _islandActivityBrush, value);
    }

    /// <summary>是否显示灵动岛活动文案。</summary>
    public bool IslandActivityVisible
    {
        get => _islandActivityVisible;
        private set => SetProperty(ref _islandActivityVisible, value);
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
    /// <summary>摄像头占用中。</summary>
    public bool IsCameraInUse
    {
        get => _isCameraInUse;
        private set => SetProperty(ref _isCameraInUse, value);
    }

    /// <summary>麦克风占用中。</summary>
    public bool IsMicInUse
    {
        get => _isMicInUse;
        private set => SetProperty(ref _isMicInUse, value);
    }

    /// <summary>正在播放音频。</summary>
    public bool IsMediaPlaying
    {
        get => _isMediaPlaying;
        private set => SetProperty(ref _isMediaPlaying, value);
    }

    /// <summary>正在截图。</summary>
    public bool IsCapturing
    {
        get => _isCapturing;
        private set => SetProperty(ref _isCapturing, value);
    }

    /// <summary>灵动岛：今日记录条数文案。</summary>
    public string IslandTaskText
    {
        get => _islandTaskText;
        private set => SetProperty(ref _islandTaskText, value);
    }

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
        RefreshIslandTaskText();

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

    private void OnSystemStatusChanged(object? sender, EventArgs e) => RefreshSystemStatus();

    /// <summary>同步摄像头 / 麦克风 / 音频 / 截图状态，并刷新灵动岛的活动文案。</summary>
    private void RefreshSystemStatus()
    {
        try
        {
            IsCameraInUse = AppServices.SystemStatus.CameraInUse;
            IsMicInUse = AppServices.SystemStatus.MicInUse;
            IsMediaPlaying = AppServices.SystemStatus.MediaPlaying;
            IsCapturing = AppServices.SystemStatus.Capturing;
        }
        catch (Exception)
        {
            // 状态探测失败时保持上一次结果。
        }

        RefreshIslandActivity();
    }

    /// <summary>
    /// 刷新灵动岛「活动状态 + 程序」文案：活动出现立即显示并打断旧的 3 秒残留倒计时，
    /// 活动结束则保留 3 秒后再消失（新活动同样可以打断这个倒计时）。
    /// </summary>
    private void RefreshIslandActivity()
    {
        IReadOnlyList<SystemActivity> activities;
        try
        {
            activities = AppServices.SystemStatus.Activities;
        }
        catch (Exception)
        {
            activities = Array.Empty<SystemActivity>();
        }

        if (activities.Count == 0)
        {
            if (IslandActivityVisible)
            {
                _activityHoldTimer.Stop();
                _activityHoldTimer.Start();
            }

            return;
        }

        _activityHoldTimer.Stop();
        IslandActivityText = ComposeActivityText(activities);
        IslandActivityBrush = ResolveActivityBrush(activities[0].Kind);
        IslandActivityVisible = true;
    }

    /// <summary>3 秒残留倒计时结束：清掉活动文案。</summary>
    private void ClearIslandActivity()
    {
        _activityHoldTimer.Stop();
        IslandActivityText = string.Empty;
        IslandActivityVisible = false;
    }

    /// <summary>把活动列表拼成「程序 · 摄像头/麦克风」，多程序时追加 +N。</summary>
    private static string ComposeActivityText(IReadOnlyList<SystemActivity> activities)
    {
        SystemActivity primary = activities[0];
        string primaryProgram = primary.Program ?? string.Empty;
        var labels = new List<string>(4);
        var otherPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SystemActivity activity in activities)
        {
            string program = activity.Program ?? string.Empty;
            if (!string.Equals(program, primaryProgram, StringComparison.OrdinalIgnoreCase))
            {
                if (program.Length > 0)
                {
                    otherPrograms.Add(program);
                }

                continue;
            }

            string label = ResolveActivityLabel(activity.Kind);
            if (!labels.Contains(label))
            {
                labels.Add(label);
            }
        }

        string labelText = string.Join("/", labels);
        string text;
        if (primaryProgram.Length > 0 && labelText.Length > 0)
        {
            text = primaryProgram + " · " + labelText;
        }
        else if (primaryProgram.Length > 0)
        {
            text = primaryProgram;
        }
        else
        {
            text = labelText;
        }

        if (otherPrograms.Count > 0)
        {
            text += " +" + otherPrograms.Count.ToString(CultureInfo.InvariantCulture);
        }

        return text;
    }

    private static string ResolveActivityLabel(SystemActivityKind kind) => kind switch
    {
        SystemActivityKind.Camera => "摄像头",
        SystemActivityKind.Microphone => "麦克风",
        SystemActivityKind.Media => "音频",
        SystemActivityKind.Capturing => "截图",
        _ => string.Empty
    };

    private static Brush ResolveActivityBrush(SystemActivityKind kind) => kind switch
    {
        SystemActivityKind.Camera => CreateFrozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8B, 0x8B))),
        SystemActivityKind.Microphone => CreateFrozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54))),
        SystemActivityKind.Media => CreateFrozen(new SolidColorBrush(Color.FromRgb(0x6B, 0xD5, 0xA0))),
        SystemActivityKind.Capturing => CreateFrozen(new SolidColorBrush(Color.FromRgb(0x6B, 0xB8, 0xFF))),
        _ => Brushes.LightSteelBlue
    };

    private static Brush CreateFrozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>刷新灵动岛上的今日记录条数。</summary>
    private void RefreshIslandTaskText()
    {
        try
        {
            int count = AppServices.TimeTracking.Today.Tasks.Count;
            IslandTaskText = count == 0 ? "今日 0 条" : "今日 " + count + " 条";
        }
        catch (Exception)
        {
            IslandTaskText = "今日 --";
        }
    }

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
        StateGlowBrush = ResolveGlowBrush(tracking.CurrentState, tracking.CurrentRestSource);
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

    /// <summary>生成状态光晕的径向渐变：中心为状态色（较亮），向外快速渐隐，形成"圆点外扩"的脉冲光晕。</summary>
    private static Brush ResolveGlowBrush(WorkState state, RestSource? restSource)
    {
        Color color = ResolveBrush(state, restSource) is SolidColorBrush solid ? solid.Color : Colors.SlateGray;
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Clear();
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0xE8), 0.0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x7A), 0.35));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x28), 0.65));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0x00), 1.0));
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
