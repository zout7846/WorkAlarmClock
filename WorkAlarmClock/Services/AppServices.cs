using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Views;

namespace WorkAlarmClock.Services;

/// <summary>
/// 组合根门面：持有 <see cref="IServiceProvider"/>，提供全局静态访问入口。
/// v1 的服务定位器重构为 IOC 容器（R-23/R-29）；所有静态属性与事件契约保持不变，
/// 因此各 ViewModel 的代码零改动。生命周期 = 应用生命周期。
/// </summary>
public static class AppServices
{
    private static IServiceProvider? _provider;

    private static ClockTicker? _ticker;
    private static StorageService? _storage;
    private static SettingsService? _settings;
    private static ScheduleService? _schedule;
    private static TimeTrackingService? _timeTracking;
    private static IdleMonitorService? _idleMonitor;
    private static ReminderService? _reminder;
    private static ReportService? _report;
    private static StatisticsService? _statistics;
    private static AutoStartService? _autoStart;
    private static NotificationService? _notification;
    private static HotkeyService? _hotkey;
    private static ScreenshotService? _screenshot;
    private static BackupService? _backup;
    private static HealthReminderService? _health;
    private static FocusService? _focus;
    private static ExportService? _export;

    /// <summary>底层容器（供本类内部与调试使用）。</summary>
    private static IServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException("AppServices 尚未初始化。");

    /// <summary>时钟节拍。</summary>
    public static ITicker Ticker => Provider.GetRequiredService<ITicker>();

    /// <summary>设置服务。</summary>
    public static ISettingsService Settings => Provider.GetRequiredService<ISettingsService>();

    /// <summary>存储服务。</summary>
    public static IStorageService Storage => Provider.GetRequiredService<IStorageService>();

    /// <summary>作息服务。</summary>
    public static IScheduleService Schedule => Provider.GetRequiredService<IScheduleService>();

    /// <summary>空闲与锁屏检测服务。</summary>
    public static IIdleMonitorService IdleMonitor => Provider.GetRequiredService<IIdleMonitorService>();

    /// <summary>工时统计服务。</summary>
    public static ITimeTrackingService TimeTracking => Provider.GetRequiredService<ITimeTrackingService>();

    /// <summary>提醒服务。</summary>
    public static IReminderService Reminder => Provider.GetRequiredService<IReminderService>();

    /// <summary>日报服务。</summary>
    public static IReportService Report => Provider.GetRequiredService<IReportService>();

    /// <summary>通知服务。</summary>
    public static INotificationService Notification => Provider.GetRequiredService<INotificationService>();

    /// <summary>统计服务。</summary>
    public static IStatisticsService Statistics => Provider.GetRequiredService<IStatisticsService>();

    /// <summary>开机自启服务。</summary>
    public static IAutoStartService AutoStart => Provider.GetRequiredService<IAutoStartService>();

    /// <summary>全局热键服务。</summary>
    public static IHotkeyService Hotkey => Provider.GetRequiredService<IHotkeyService>();

    /// <summary>区域截图服务。</summary>
    public static IScreenshotService Screenshot => Provider.GetRequiredService<IScreenshotService>();

    /// <summary>数据库备份服务。</summary>
    public static IBackupService Backup => Provider.GetRequiredService<IBackupService>();

    /// <summary>健康提醒服务（久坐/喝水）。</summary>
    public static IHealthReminderService Health => Provider.GetRequiredService<IHealthReminderService>();

    /// <summary>番茄钟服务。</summary>
    public static IFocusService Focus => Provider.GetRequiredService<IFocusService>();

    /// <summary>数据导出服务。</summary>
    public static IExportService Export => Provider.GetRequiredService<IExportService>();

    /// <summary>倒计时服务。</summary>
    public static ICountdownService Countdown => Provider.GetRequiredService<ICountdownService>();

    /// <summary>网络状态监测。</summary>
    public static INetworkMonitor Network => Provider.GetRequiredService<INetworkMonitor>();

    /// <summary>天气服务。</summary>
    public static IWeatherService Weather => Provider.GetRequiredService<IWeatherService>();

    /// <summary>剪贴板历史。</summary>
    public static IClipboardHistoryService ClipboardHistory => Provider.GetRequiredService<IClipboardHistoryService>();

    /// <summary>网络测速。</summary>
    public static ISpeedTestService SpeedTest => Provider.GetRequiredService<ISpeedTestService>();

    /// <summary>系统状态（摄像头 / 麦克风 / 音频 / 截图）。</summary>
    public static SystemStatusService SystemStatus => Provider.GetRequiredService<SystemStatusService>();

    /// <summary>桌宠气泡请求事件（桌宠窗口订阅）。</summary>
    public static event EventHandler<BubbleRequest>? BubbleRequested;

    /// <summary>桌宠透明度实时预览事件（设置面板拖动滑条时触发，桌宠窗口订阅）。</summary>
    public static event EventHandler<double>? LiveOpacityPreview;

    /// <summary>桌宠显示/隐藏切换请求事件（热键、托盘菜单、主面板共用，由 App 订阅控制窗口）。</summary>
    public static event Action? PetToggleRequested;

    /// <summary>请求切换桌宠显示/隐藏（热键与菜单共用）。</summary>
    public static void RaisePetToggle() => PetToggleRequested?.Invoke();

    /// <summary>触发桌宠透明度实时预览（保存前不落盘）。</summary>
    /// <param name="opacity">预览透明度值。</param>
    public static void RaiseLiveOpacityPreview(double opacity)
    {
        LiveOpacityPreview?.Invoke(null, opacity);
    }

    /// <summary>服务是否已完成初始化。</summary>
    public static bool IsInitialized => _provider is not null;

    /// <summary>
    /// 初始化组合根：构建容器 → 解析全部服务 → 落位 → 接线 → 启动各服务。
    /// </summary>
    public static void Initialize()
    {
        if (_provider is not null)
        {
            return;
        }

        AppEnvironment.EnsureDirectories();

        // 测试钩子：设置环境变量 WORKALARM_TEST_NOW（ISO 8601）可注入固定时钟，
        // 用于跨时间点启动落位等场景的真机模拟验证；正常启动不受影响。
        Func<DateTimeOffset>? testClock = ResolveTestClock();
        if (testClock is not null)
        {
            AppEnvironment.LogWarning("检测到 WORKALARM_TEST_NOW 时间注入（仅限测试场景使用）。");
        }

        ServiceCollection services = new ServiceCollection();
        ConfigureServices(services, testClock);
        _provider = services.BuildServiceProvider();

        // 解析全部单例并缓存到静态字段，便于事件接线与生命周期管理。
        _ticker = _provider.GetRequiredService<ClockTicker>();
        _storage = _provider.GetRequiredService<StorageService>();
        _settings = _provider.GetRequiredService<SettingsService>();
        _schedule = _provider.GetRequiredService<ScheduleService>();
        _timeTracking = _provider.GetRequiredService<TimeTrackingService>();
        _idleMonitor = _provider.GetRequiredService<IdleMonitorService>();
        _reminder = _provider.GetRequiredService<ReminderService>();
        _report = _provider.GetRequiredService<ReportService>();
        _statistics = _provider.GetRequiredService<StatisticsService>();
        _autoStart = _provider.GetRequiredService<AutoStartService>();
        _notification = _provider.GetRequiredService<NotificationService>();
        _hotkey = _provider.GetRequiredService<HotkeyService>();
        _screenshot = _provider.GetRequiredService<ScreenshotService>();
        _backup = _provider.GetRequiredService<BackupService>();
        _health = _provider.GetRequiredService<HealthReminderService>();
        _focus = _provider.GetRequiredService<FocusService>();
        _export = _provider.GetRequiredService<ExportService>();

        DateOnly today = DateOnly.FromDateTime(_ticker.Now.LocalDateTime);
        _storage.EnsureArchive(today);
        _timeTracking.LoadOrCreateToday();

        // 每日首次启动备份数据库（VACUUM INTO 快照，独立目录，不影响主库）。
        _backup.RunIfDue(today);

        // 落位后读回当日提醒状态，避免重启后重复弹弱提醒（R-25 修复）。
        _reminder.InitializeState(today);

        WireEvents();

        _idleMonitor.Start();
        _reminder.Start();
        _health.Start();
        _ticker.Start();

        // 工具类服务：网络、天气、剪贴板历史（后台静默运行，失败不影响主流程）。
        if (_provider.GetRequiredService<ISettingsService>().Current.NetworkMonitorEnabled)
        {
            _provider.GetRequiredService<INetworkMonitor>().Start();
        }

        _provider.GetRequiredService<IWeatherService>().Start();
        _provider.GetRequiredService<IClipboardHistoryService>().Start();

        // 设置中的自启开关以注册表实际状态为准，启动时对齐一次。
        if (_settings.Current.AutoStart != _autoStart.IsEnabled())
        {
            _autoStart.SetEnabled(_settings.Current.AutoStart);
        }

        AppEnvironment.Log("AppServices 初始化完成（IOC 容器）。");
    }

    /// <summary>集中注册全部服务（单例，与 v1 组合根一致）。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="testClock">测试时钟注入（WORKALARM_TEST_NOW），正常启动为 null。</param>
    private static void ConfigureServices(ServiceCollection services, Func<DateTimeOffset>? testClock = null)
    {
        // 同时注册具体类与接口，并让接口转发到同一单例实例：
        // - 具体类注册供 AppServices.Initialize 按具体类型解析（与 v1 字段类型一致）；
        // - 接口转发保证 IStorageService 等属性与具体类解析得到同一个实例，避免重复构造单例。
        // 时钟源必须在这里注入，否则 WORKALARM_TEST_NOW 只打日志、不生效。
        services.AddSingleton(sp => new ClockTicker(testClock));
        services.AddSingleton<ITicker>(sp => sp.GetRequiredService<ClockTicker>());
        services.AddSingleton<StorageService>();
        services.AddSingleton<IStorageService>(sp => sp.GetRequiredService<StorageService>());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ScheduleService>();
        services.AddSingleton<IScheduleService>(sp => sp.GetRequiredService<ScheduleService>());
        services.AddSingleton<TimeTrackingService>();
        services.AddSingleton<ITimeTrackingService>(sp => sp.GetRequiredService<TimeTrackingService>());
        services.AddSingleton<IdleMonitorService>();
        services.AddSingleton<IIdleMonitorService>(sp => sp.GetRequiredService<IdleMonitorService>());
        services.AddSingleton<ReminderService>();
        services.AddSingleton<IReminderService>(sp => sp.GetRequiredService<ReminderService>());
        services.AddSingleton<ReportService>();
        services.AddSingleton<IReportService>(sp => sp.GetRequiredService<ReportService>());
        services.AddSingleton<StatisticsService>();
        services.AddSingleton<IStatisticsService>(sp => sp.GetRequiredService<StatisticsService>());
        services.AddSingleton<AutoStartService>();
        services.AddSingleton<IAutoStartService>(sp => sp.GetRequiredService<AutoStartService>());
        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<HotkeyService>());
        services.AddSingleton<ScreenshotService>();
        services.AddSingleton<IScreenshotService>(sp => sp.GetRequiredService<ScreenshotService>());
        services.AddSingleton<BackupService>();
        services.AddSingleton<IBackupService>(sp => sp.GetRequiredService<BackupService>());
        services.AddSingleton<HealthReminderService>();
        services.AddSingleton<IHealthReminderService>(sp => sp.GetRequiredService<HealthReminderService>());
        services.AddSingleton<FocusService>();
        services.AddSingleton<IFocusService>(sp => sp.GetRequiredService<FocusService>());
        services.AddSingleton<ExportService>();
        services.AddSingleton<IExportService>(sp => sp.GetRequiredService<ExportService>());
        services.AddSingleton<CountdownService>();
        services.AddSingleton<ICountdownService>(sp => sp.GetRequiredService<CountdownService>());
        services.AddSingleton<NetworkMonitorService>();
        services.AddSingleton<INetworkMonitor>(sp => sp.GetRequiredService<NetworkMonitorService>());
        services.AddSingleton<WeatherService>();
        services.AddSingleton<IWeatherService>(sp => sp.GetRequiredService<WeatherService>());
        services.AddSingleton<ClipboardHistoryService>();
        services.AddSingleton<IClipboardHistoryService>(sp => sp.GetRequiredService<ClipboardHistoryService>());
        services.AddSingleton<SpeedTestService>();
        services.AddSingleton<ISpeedTestService>(sp => sp.GetRequiredService<SpeedTestService>());
        services.AddSingleton<SystemStatusService>();
    }

    /// <summary>接线托盘菜单命令（需要在 MainViewModel 创建之后调用）。</summary>
    /// <param name="callbacks">托盘菜单回调。</param>
    public static void AttachTray(TrayMenuCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        _notification?.InitializeTray(callbacks);
    }

    /// <summary>统一退出清理：停止检测、解绑事件、释放托盘与钩子、释放容器。</summary>
    public static void Shutdown()
    {
        try
        {
            // 1) 退订 AppServices 层事件转发。
            if (_ticker is not null)
            {
                _ticker.Tick -= OnTick;
            }

            if (_settings is not null)
            {
                _settings.SettingsChanged -= OnSettingsChanged;
            }

            if (_timeTracking is not null)
            {
                _timeTracking.StateChanged -= OnStateChanged;
                _timeTracking.DayRolled -= OnDayRolled;
            }

            if (_idleMonitor is not null)
            {
                _idleMonitor.Locked -= OnIdleLocked;
                _idleMonitor.Unlocked -= OnIdleUnlocked;
                _idleMonitor.IdleDetected -= OnIdleDetected;
                _idleMonitor.ActivityResumed -= OnActivityResumed;
                _idleMonitor.Stop();
            }

            if (_reminder is not null)
            {
                _reminder.ReminderDue -= OnReminderDue;
                _reminder.Stop();
            }

            if (_notification is not null)
            {
                _notification.BubbleRequested -= OnBubbleRequested;
                _notification.Dispose();
            }

            // 2) 卸载全局键盘钩子（IDisposable 安全网之外再显式卸载一次）。
            _hotkey?.Uninstall();

            // 2.5) 关闭可能残留的截图遮罩，释放冻结位图。
            _screenshot?.CloseOverlay();

            // 2.6) 停止健康提醒计时器。
            _health?.Dispose();

            // 2.7) 停止番茄钟并退订时钟节拍。
            _focus?.Dispose();

            // 2.8) 停止工具类服务（网络/天气/剪贴板/倒计时）。
            _provider?.GetService<INetworkMonitor>()?.Stop();
            _provider?.GetService<IWeatherService>()?.Stop();
            _provider?.GetService<IClipboardHistoryService>()?.Stop();
            (_provider?.GetService<ICountdownService>() as IDisposable)?.Dispose();

            // 3) 释放容器：逆序释放各单例 IDisposable（Storage 关连接、Notification 释放图标等）。
            (_provider as IDisposable)?.Dispose();
            _provider = null;

            AppEnvironment.Log("AppServices 已清理。");
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("AppServices 清理过程出现异常。", ex);
        }
    }

    /// <summary>请求退出程序：清理服务后关闭应用。</summary>
    public static void RequestExit()
    {
        Shutdown();
        System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>把占位符 "-" / 空白归一化为空串，交给渲染层走"自动汇总"回退。</summary>
    /// <param name="value">原始值。</param>
    /// <returns>归一化文本。</returns>
    private static string NormalizePlaceholder(string? value) =>
        value is null || value.Trim() == "-" ? string.Empty : value.Trim();

    /// <summary>立即生成日报（复制剪贴板 + 气泡反馈），供主面板、托盘与提醒共用。</summary>
    public static void GenerateReportNow()
    {
        try
        {
            // 复用上次预览窗保存的"今日工作/明日计划/风险"，避免快捷生成把它们清空。
            ReportRecord? last = TimeTracking.Today.Reports.LastOrDefault();
            string todayWork = NormalizePlaceholder(last?.TodayWork);
            string tomorrowPlan = NormalizePlaceholder(last?.TomorrowPlan);
            string risks = NormalizePlaceholder(last?.Risks);

            string content = Report.RenderText(todayWork, tomorrowPlan, risks);
            ReportRecord record = Report.Generate(todayWork, tomorrowPlan, risks);
            bool copied = Report.CopyToClipboard(content);
            Reminder.OnReportGenerated();
            Notification.ShowBubble(
                copied ? "日报已生成并复制到剪贴板。" : "日报已生成：" + record.FilePath,
                BubbleKind.Info);
            Notification.ShowTrayBalloon("工作日报", "日报已生成：" + record.FilePath);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("生成日报失败。", ex);
            Notification.ShowBubble("生成日报失败：" + ex.Message, BubbleKind.Info);
        }
    }

    /// <summary>拼接托盘 tooltip：状态 + 时间 + 已工作时长。</summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>tooltip 文本。</returns>
    public static string BuildTrayTooltip(DateTimeOffset now)
    {
        string state = ScheduleService.StateToText(TimeTracking.CurrentState, TimeTracking.CurrentRestSource);
        return string.Concat(state, " ", TextFormats.TimeToHm(now), " 已工作", TextFormats.DurationToHm(TimeTracking.WorkDuration));
    }

    /// <summary>
    /// 解析测试时钟注入（WORKALARM_TEST_NOW，ISO 8601）：以注入时刻为基准、按真实速率前进；
    /// 未设置或格式非法返回 null。
    /// </summary>
    private static Func<DateTimeOffset>? ResolveTestClock()
    {
        string? raw = Environment.GetEnvironmentVariable("WORKALARM_TEST_NOW");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return null;
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        return () => parsed + stopwatch.Elapsed;
    }

    private static void WireEvents()
    {
        if (_ticker is not null)
        {
            _ticker.Tick += OnTick;
        }

        if (_settings is not null)
        {
            _settings.SettingsChanged += OnSettingsChanged;
        }

        if (_timeTracking is not null)
        {
            _timeTracking.StateChanged += OnStateChanged;
            _timeTracking.DayRolled += OnDayRolled;
        }

        if (_idleMonitor is not null)
        {
            _idleMonitor.Locked += OnIdleLocked;
            _idleMonitor.Unlocked += OnIdleUnlocked;
            _idleMonitor.IdleDetected += OnIdleDetected;
            _idleMonitor.ActivityResumed += OnActivityResumed;
        }

        if (_reminder is not null)
        {
            _reminder.ReminderDue += OnReminderDue;
        }

        if (_notification is not null)
        {
            _notification.BubbleRequested += OnBubbleRequested;
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        DateTimeOffset now = Ticker.Now;
        Notification.UpdateTray(TimeTracking.CurrentState, BuildTrayTooltip(now));
    }

    private static void OnStateChanged(object? sender, WorkState state)
    {
        Notification.UpdateTray(state, BuildTrayTooltip(Ticker.Now));
    }

    /// <summary>跨午夜：气泡提示新的一天，昨日记录已留置归档。</summary>
    private static void OnDayRolled(object? sender, EventArgs e)
    {
        Notification.ShowBubble("新的一天开始啦，昨天的记录已自动归档。", BubbleKind.Info);
        Notification.ShowTrayBalloon("WorkAlarmClock", "新的一天开始啦，昨天的记录已自动归档。");
    }

    private static void OnSettingsChanged(object? sender, EventArgs e)
    {
        AppSettings settings = Settings.Current;
        AppEnvironment.Log("设置已更新，通知各服务重读。");
        try
        {
            if (AutoStart.IsEnabled() != settings.AutoStart)
            {
                AutoStart.SetEnabled(settings.AutoStart);
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("同步开机自启设置失败。", ex);
        }

        Notification.UpdateTray(TimeTracking.CurrentState, BuildTrayTooltip(Ticker.Now));
    }

    private static void OnIdleLocked(object? sender, EventArgs e)
    {
        TimeTracking.OnLocked();
    }

    private static void OnIdleUnlocked(object? sender, EventArgs e)
    {
        TimeTracking.OnUnlocked();
        ShowResumeBubbleIfNeeded();
    }

    private static void OnIdleDetected(object? sender, EventArgs e)
    {
        TimeTracking.OnIdleDetected();
    }

    private static void OnActivityResumed(object? sender, EventArgs e)
    {
        TimeTracking.OnActivityResumed();
        ShowResumeBubbleIfNeeded();
    }

    private static void ShowResumeBubbleIfNeeded()
    {
        if (TimeTracking.CurrentState != WorkState.Working)
        {
            return;
        }

        Notification.ShowBubble(
            "已恢复工作，点此纠正",
            BubbleKind.RestResume,
            "点此纠正",
            () =>
            {
                TimeTracking.TryManualSwitch(WorkState.Resting, out string? _);
            });
    }

    private static void OnReminderDue(object? sender, ReminderKind kind)
    {
        if (kind == ReminderKind.Weak)
        {
            Notification.PlaySound(SoundKind.Weak);
            Notification.ShowTrayBalloon("工作日报提醒", "快下班啦，今天的工作还没写进日报～");

            if (TimeTracking.Today.Reports.Count > 0)
            {
                // 当天已生成过日报：只做通知，不再打扰。
                Notification.ShowBubble("今天已经生成过日报，如需更新可再次生成。", BubbleKind.ReminderWeak);
                return;
            }

            List<BubbleAction> actions = new List<BubbleAction>
            {
                new BubbleAction("立即生成", DialogManager.ShowReportPreview),
                new BubbleAction("稍后提醒", () => Reminder.DeferWeak(TimeSpan.FromMinutes(10))),
                new BubbleAction("忽略", () => Reminder.IgnoreWeak())
            };
            Notification.ShowBubbleWithActions("快下班啦，该写工作日报了！", BubbleKind.ReminderWeak, actions);
            return;
        }

        // 强提醒：全屏遮罩模态窗（未确认不可关闭，重复提示音与闪烁由窗口内处理）。
        Notification.PlaySound(SoundKind.Strong);
        Notification.ShowTrayBalloon("打卡提醒", "下班时间快到了，请立即打卡！");
        DialogManager.ShowStrongReminder();
    }

    private static void OnBubbleRequested(object? sender, BubbleRequest request)
    {
        if (string.IsNullOrEmpty(request.Message))
        {
            return;
        }

        BubbleRequested?.Invoke(sender, request);
    }
}
