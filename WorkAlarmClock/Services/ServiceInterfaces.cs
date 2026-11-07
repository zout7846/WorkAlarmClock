using System;
using System.Collections.Generic;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

// 注意：ITicker 与 ClockTicker 定义于 Infrastructure/MvvmCore.cs（同属 WorkAlarmClock.Services 命名空间），
// 集中管理"时钟 + MVVM 基础设施"这两块无业务依赖的代码，此处不再重复声明。

/// <summary>设置校验结果。</summary>
/// <param name="Ok">是否通过校验。</param>
/// <param name="Error">错误或警告文案（中文短句），通过时为 null 或警告文本。</param>
public readonly record struct ValidationResult(bool Ok, string? Error);

/// <summary>作息时间点。</summary>
/// <param name="Time">日内时刻。</param>
/// <param name="Kind">时间点类型。</param>
public readonly record struct SchedulePoint(TimeOnly Time, SchedulePointKind Kind);

/// <summary>
/// 设置服务：设置的加载、校验、保存与变更广播。
/// </summary>
public interface ISettingsService
{
    /// <summary>当前生效的设置。</summary>
    AppSettings Current { get; }

    /// <summary>设置变更事件（UI 线程）。</summary>
    event EventHandler? SettingsChanged;

    /// <summary>校验候选设置。</summary>
    /// <param name="candidate">候选设置。</param>
    /// <returns>校验结果。</returns>
    ValidationResult Validate(AppSettings candidate);

    /// <summary>校验通过则保存并广播变更。</summary>
    /// <param name="candidate">候选设置。</param>
    /// <returns>校验结果；失败时不保存。</returns>
    ValidationResult TryUpdate(AppSettings candidate);
}

/// <summary>
/// 存储服务：settings.json 与 data/YYYY-MM-DD.json 的读写。
/// </summary>
public interface IStorageService
{
    /// <summary>加载设置；文件缺失或损坏时返回默认设置。</summary>
    /// <returns>设置实例。</returns>
    AppSettings LoadSettings();

    /// <summary>原子写入设置文件。</summary>
    /// <param name="settings">设置实例。</param>
    void SaveSettings(AppSettings settings);

    /// <summary>加载指定日期的记录；不存在返回 null。</summary>
    /// <param name="date">日期。</param>
    /// <returns>当日记录或 null。</returns>
    DayRecord? LoadDay(DateOnly date);

    /// <summary>原子写入当日记录。</summary>
    /// <param name="record">当日记录。</param>
    void SaveDay(DayRecord record);

    /// <summary>读取本周（周一至今）的每日记录。</summary>
    /// <param name="today">今天。</param>
    /// <returns>记录列表（缺失日期跳过）。</returns>
    IReadOnlyList<DayRecord> LoadWeek(DateOnly today);

    /// <summary>跨天检查：确保数据库上下文已就绪（SQLite 数据常驻库内，无需搬运文件）。</summary>
    /// <param name="today">今天。</param>
    void EnsureArchive(DateOnly today);

    /// <summary>读取指定日期的提醒状态（弱/强提醒触发与确认标记）；无记录返回 null。</summary>
    /// <param name="date">日期。</param>
    /// <returns>提醒状态或 null。</returns>
    ReminderState? LoadReminderState(DateOnly date);

    /// <summary>持久化指定日期的提醒状态（幂等 upsert）。</summary>
    /// <param name="date">日期。</param>
    /// <param name="state">提醒状态。</param>
    void SaveReminderState(DateOnly date, ReminderState state);

    /// <summary>把数据库一致性快照写入指定文件（VACUUM INTO，目标文件必须不存在）。</summary>
    /// <param name="filePath">目标文件路径。</param>
    void BackupTo(string filePath);

    /// <summary>列出有日报记录的日期（倒序，最多 <paramref name="max"/> 条）。</summary>
    /// <param name="max">最大条数。</param>
    /// <returns>日期列表。</returns>
    IReadOnlyList<DateOnly> ListReportDates(int max);

    /// <summary>列出有记录的日期（倒序，最多 <paramref name="max"/> 条）。</summary>
    /// <param name="max">最大条数。</param>
    /// <returns>日期列表。</returns>
    IReadOnlyList<DateOnly> ListDayDates(int max);
}

/// <summary>
/// 作息服务：工作日判定与状态推算。
/// </summary>
public interface IScheduleService
{
    /// <summary>指定日期是否为工作日。</summary>
    /// <param name="date">日期。</param>
    /// <returns>true 表示工作日。</returns>
    bool IsWorkday(DateOnly date);

    /// <summary>按作息推算"现在理应处于的状态"（不看流水与手动锁）。</summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>期望状态。</returns>
    WorkState GetExpectedState(DateTimeOffset now);

    /// <summary>指定日期的全部作息时间点（升序；午休关闭时不含午休两点）。</summary>
    /// <param name="date">日期。</param>
    /// <returns>时间点列表。</returns>
    IReadOnlyList<SchedulePoint> GetTodayPoints(DateOnly date);

    /// <summary>下一个自动切换时间点；找不到（长期非工作日）返回 null。</summary>
    /// <param name="now">当前时刻。</param>
    /// <returns>下一切换时刻或 null。</returns>
    DateTimeOffset? NextTransitionAt(DateTimeOffset now);
}

/// <summary>
/// 工时统计服务：状态机核心与今日数据维护。
/// </summary>
public interface ITimeTrackingService
{
    /// <summary>当前状态。</summary>
    WorkState CurrentState { get; }

    /// <summary>当前休息来源（非休息状态为 null）。</summary>
    RestSource? CurrentRestSource { get; }

    /// <summary>是否已手动结束当天（终态锁）。</summary>
    bool ManualEnded { get; }

    /// <summary>当日记录。</summary>
    DayRecord Today { get; }

    /// <summary>今日工作时长（流水派生）。</summary>
    TimeSpan WorkDuration { get; }

    /// <summary>今日休息时长（流水派生）。</summary>
    TimeSpan RestDuration { get; }

    /// <summary>状态变更事件（UI 线程）。</summary>
    event EventHandler<WorkState>? StateChanged;

    /// <summary>事项变更事件（UI 线程）。</summary>
    event EventHandler? TasksChanged;

    /// <summary>运行中跨午夜事件（新的一天已落位，旧记录已归档）。</summary>
    event EventHandler? DayRolled;

    /// <summary>启动或跨天时调用：加载当日记录，无流水时按作息写入初始落位。</summary>
    void LoadOrCreateToday();

    /// <summary>到达作息点时落位（受终态锁静默约束）。</summary>
    /// <param name="kind">时间点类型。</param>
    void ApplySchedulePoint(SchedulePointKind kind);

    /// <summary>手动切换状态（优先级最高）。</summary>
    /// <param name="target">目标状态。</param>
    /// <param name="error">失败原因；成功时为 null。</param>
    /// <returns>是否切换成功。</returns>
    bool TryManualSwitch(WorkState target, out string? error);

    /// <summary>系统锁屏。</summary>
    void OnLocked();

    /// <summary>系统解锁。</summary>
    void OnUnlocked();

    /// <summary>检测到键鼠空闲超阈值。</summary>
    void OnIdleDetected();

    /// <summary>检测到键鼠恢复活动。</summary>
    void OnActivityResumed();

    /// <summary>添加今日事项。</summary>
    /// <param name="text">事项文本。</param>
    void AddTask(string text);

    /// <summary>添加一条带标签的今日事项。</summary>
    /// <param name="text">事项文本。</param>
    /// <param name="tag">标签（空串表示未分类）。</param>
    void AddTask(string text, string tag);

    /// <summary>删除今日事项。</summary>
    /// <param name="id">事项标识。</param>
    /// <returns>是否删除成功。</returns>
    bool RemoveTask(Guid id);

    /// <summary>重算工作/休息时长。</summary>
    void RefreshDurations();
}

/// <summary>
/// 空闲与锁屏检测服务。
/// </summary>
public interface IIdleMonitorService
{
    /// <summary>当前是否锁屏中。</summary>
    bool IsLocked { get; }

    /// <summary>当前是否空闲中。</summary>
    bool IsIdle { get; }

    /// <summary>当前已空闲时长。</summary>
    TimeSpan CurrentIdleSpan { get; }

    /// <summary>锁屏事件。</summary>
    event EventHandler? Locked;

    /// <summary>解锁事件。</summary>
    event EventHandler? Unlocked;

    /// <summary>进入空闲事件（边沿触发）。</summary>
    event EventHandler? IdleDetected;

    /// <summary>恢复活动事件（边沿触发）。</summary>
    event EventHandler? ActivityResumed;

    /// <summary>启动检测。</summary>
    void Start();

    /// <summary>停止检测并退订系统事件。</summary>
    void Stop();
}

/// <summary>
/// 提醒调度服务：弱提醒（写日报）与强提醒（打卡）。
/// </summary>
public interface IReminderService
{
    /// <summary>当日提醒进度。</summary>
    ReminderState State { get; }

    /// <summary>提醒到期事件（UI 线程）。</summary>
    event EventHandler<ReminderKind>? ReminderDue;

    /// <summary>启动调度。</summary>
    void Start();

    /// <summary>停止调度。</summary>
    void Stop();

    /// <summary>弱提醒稍后提醒。</summary>
    /// <param name="delay">延迟时长。</param>
    void DeferWeak(TimeSpan delay);

    /// <summary>忽略当日弱提醒。</summary>
    void IgnoreWeak();

    /// <summary>确认强提醒（已打卡）。</summary>
    void AcknowledgeStrong();

    /// <summary>当日已生成日报。</summary>
    void OnReportGenerated();

    /// <summary>跨天重置提醒状态。</summary>
    void ResetForNewDay();
}

    /// <summary>
    /// 日报服务：渲染、落盘、复制。
    /// </summary>
    public interface IReportService
    {
        /// <summary>内置默认模板（设置页「恢复默认」使用）。</summary>
        string DefaultReportTemplate { get; }

        /// <summary>渲染纯文本日报（不落盘，按模板渲染）。</summary>
        /// <param name="todayWork">今日工作（可编辑；为空则回退自动事项汇总）。</param>
        /// <param name="tomorrowPlan">明日计划。</param>
        /// <param name="risks">风险与待办。</param>
        /// <returns>纯文本日报内容。</returns>
        string RenderText(string todayWork, string tomorrowPlan, string risks);

        /// <summary>渲染并落盘（.txt 覆盖写，同日不再生成 .bak）。</summary>
        /// <param name="todayWork">今日工作（可编辑；为空则回退自动事项汇总）。</param>
        /// <param name="tomorrowPlan">明日计划。</param>
        /// <param name="risks">风险与待办。</param>
        /// <returns>生成记录。</returns>
        ReportRecord Generate(string todayWork, string tomorrowPlan, string risks);

    /// <summary>复制文本到剪贴板。</summary>
    /// <param name="content">文本内容。</param>
    /// <returns>是否成功。</returns>
    bool CopyToClipboard(string content);

    /// <summary>解析日报目录（空配置回退默认目录）。</summary>
    /// <returns>目录路径。</returns>
    string ResolveReportDirectory();

        /// <summary>指定日期的日报文件路径。</summary>
        /// <param name="date">日期。</param>
        /// <returns>文件路径。</returns>
        string GetReportPath(DateOnly date);

        /// <summary>生成指定日期所在周的周报（周一至周日），落盘并返回内容与路径。</summary>
        /// <param name="anyDayInWeek">本周任意一天。</param>
        /// <returns>周报内容与主文件路径。</returns>
        WeeklyReport GenerateWeekly(DateOnly anyDayInWeek);
}

/// <summary>
/// 区域截图服务：冻结全屏 → 遮罩选区 → 裁剪保存 PNG（可选复制剪贴板）。
/// </summary>
public interface IScreenshotService
{
    /// <summary>是否正在选区中。</summary>
    bool IsCapturing { get; }

    /// <summary>开始区域截图（重复调用聚焦已有遮罩）。</summary>
    void StartCapture();

    /// <summary>关闭遮罩并释放冻结位图（退出时调用）。</summary>
    void CloseOverlay();

    /// <summary>解析截图目录（空配置回退默认目录）。</summary>
    /// <returns>目录路径。</returns>
    string ResolveScreenshotDirectory();
}

/// <summary>
/// 通知服务：托盘图标、气泡与提示音（UI 边界服务）。
/// </summary>
public interface INotificationService
{
    /// <summary>显示桌宠气泡；可选带一个点击动作。</summary>
    /// <param name="message">气泡文本。</param>
    /// <param name="kind">气泡类型。</param>
    /// <param name="actionLabel">动作按钮文案，null 表示无按钮。</param>
    /// <param name="action">动作回调。</param>
    void ShowBubble(string message, BubbleKind kind, string? actionLabel = null, Action? action = null);

    /// <summary>显示带多个动作按钮的桌宠气泡（弱提醒的立即生成/稍后提醒/忽略）。</summary>
    /// <param name="message">气泡文本。</param>
    /// <param name="kind">气泡类型。</param>
    /// <param name="actions">动作按钮集合，为空表示纯文本气泡。</param>
    void ShowBubbleWithActions(string message, BubbleKind kind, IReadOnlyList<BubbleAction> actions);

    /// <summary>显示托盘气泡通知。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">内容。</param>
    void ShowTrayBalloon(string title, string message);

    /// <summary>播放提示音。</summary>
    /// <param name="kind">提示音类型。</param>
    void PlaySound(SoundKind kind);

    /// <summary>更新托盘图标与 tooltip（内部节流）。</summary>
    /// <param name="state">当前状态。</param>
    /// <param name="tooltip">tooltip 文本。</param>
    void UpdateTray(WorkState state, string tooltip);

    /// <summary>初始化托盘图标与右键菜单。</summary>
    /// <param name="callbacks">菜单回调集合。</param>
    void InitializeTray(TrayMenuCallbacks callbacks);

    /// <summary>显示主面板（托盘双击或菜单）。</summary>
    void ShowMainPanel();
}

/// <summary>
/// 托盘菜单回调集合（命令由 MainViewModel 提供）。
/// </summary>
/// <param name="openMainPanel">打开主面板。</param>
/// <param name="startWork">开始工作。</param>
/// <param name="startRest">开始休息。</param>
/// <param name="endDay">结束当天。</param>
/// <param name="generateReport">生成工作汇报。</param>
/// <param name="focusToggle">开始/停止专注。</param>
/// <param name="openSettings">打开设置。</param>
/// <param name="togglePet">显示/隐藏桌宠。</param>
/// <param name="screenshot">区域截图。</param>
/// <param name="exitApp">退出程序。</param>
public sealed record TrayMenuCallbacks(
    Action openMainPanel,
    Action startWork,
    Action startRest,
    Action endDay,
    Action generateReport,
    Action focusToggle,
    Action openSettings,
    Action togglePet,
    Action screenshot,
    Action exitApp);

/// <summary>
/// 统计服务：今日与本周聚合。
/// </summary>
public interface IStatisticsService
{
    /// <summary>获取今日与本周统计。</summary>
    /// <returns>统计结果。</returns>
    WeekStats GetStats();

    /// <summary>获取最近 N 天（含今天）的每日统计，按日期升序。</summary>
    /// <param name="days">天数（1–31）。</param>
    /// <returns>每日统计列表。</returns>
    IReadOnlyList<DayStat> GetRecentDays(int days);

    /// <summary>获取今天的时间轴分段（工作/休息/午休/下班），已合并相邻同色段。</summary>
    /// <returns>时间轴分段列表（可能为空）。</returns>
    IReadOnlyList<TimelineSlice> GetTodayTimeline();
}

/// <summary>
/// 某一天的工作/休息统计。
/// </summary>
/// <param name="Date">日期。</param>
/// <param name="Work">工作时长。</param>
/// <param name="Rest">休息时长。</param>
/// <param name="IsWorkday">是否工作日。</param>
public sealed record DayStat(DateOnly Date, TimeSpan Work, TimeSpan Rest, bool IsWorkday);

/// <summary>
/// 时间轴分段。
/// </summary>
/// <param name="Start">开始时刻。</param>
/// <param name="End">结束时刻。</param>
/// <param name="Kind">分段类型（work / lunch / rest / off）。</param>
/// <param name="Label">展示文案。</param>
public sealed record TimelineSlice(DateTimeOffset Start, DateTimeOffset End, string Kind, string Label);

/// <summary>
/// 统计结果。
/// </summary>
/// <param name="TodayWork">今日工作时长。</param>
/// <param name="TodayRest">今日休息时长。</param>
/// <param name="WeekWork">本周（工作日）工作时长。</param>
/// <param name="WeekRest">本周（工作日）休息时长。</param>
public sealed record WeekStats(TimeSpan TodayWork, TimeSpan TodayRest, TimeSpan WeekWork, TimeSpan WeekRest);

/// <summary>
/// 周报生成结果。
/// </summary>
/// <param name="Content">周报纯文本内容。</param>
/// <param name="FilePath">主文件路径（.txt 优先）。</param>
public sealed record WeeklyReport(string Content, string FilePath);

/// <summary>
/// 开机自启服务（注册表 HKCU\...\Run）。
/// </summary>
/// <summary>
/// 番茄钟阶段。
/// </summary>
public enum FocusPhase
{
    /// <summary>未开始。</summary>
    Idle,

    /// <summary>专注中。</summary>
    Focus,

    /// <summary>短休息。</summary>
    Break,

    /// <summary>长休息。</summary>
    LongBreak
}

/// <summary>
/// 番茄钟服务：专注 / 短休息 / 长休息三阶段倒计时（基于全局 Ticker，秒级推进）。
/// </summary>
public interface IFocusService
{
    /// <summary>当前阶段。</summary>
    FocusPhase Phase { get; }

    /// <summary>本阶段剩余时长。</summary>
    TimeSpan Remaining { get; }

    /// <summary>今天已完成的专注轮数。</summary>
    int CompletedRounds { get; }

    /// <summary>是否正在专注（休息阶段不算）。</summary>
    bool IsFocusing { get; }

    /// <summary>是否处于任何计时阶段。</summary>
    bool IsRunning { get; }

    /// <summary>阶段或剩余时间变化事件（每秒触发）。</summary>
    event EventHandler? StateChanged;

    /// <summary>开始一轮专注。</summary>
    void Start();

    /// <summary>停止并回到空闲。</summary>
    void Stop();

    /// <summary>跳过当前阶段（专注→休息 / 休息→空闲）。</summary>
    void Skip();
}

/// <summary>
/// 健康提醒服务：久坐与喝水提醒（仅在工作状态下计时）。
/// </summary>
public interface IHealthReminderService
{
    /// <summary>启动计时。</summary>
    void Start();

    /// <summary>停止计时。</summary>
    void Stop();

    /// <summary>重置久坐计时（例如刚起身回来）。</summary>
    void ResetSitTimer();
}

/// <summary>
/// 数据导出服务：把工时、事项、日报清单导出为 CSV（零依赖手写 CSV）。
/// </summary>
public interface IExportService
{
    /// <summary>默认导出根目录（我的文档\WorkAlarmClock\Export）。</summary>
    string ExportRoot { get; }

    /// <summary>导出全部数据到指定目录（null 表示默认目录下的时间戳子目录）。</summary>
    /// <param name="directory">目标目录。</param>
    /// <returns>生成的文件路径列表；失败返回空列表。</returns>
    IReadOnlyList<string> ExportAll(string? directory = null);
}

/// <summary>
/// 数据库备份服务：每日生成一份快照并按保留天数清理。
/// </summary>
public interface IBackupService
{
    /// <summary>备份目录。</summary>
    string BackupDirectory { get; }

    /// <summary>若今天还没备份则执行一次（并清理过期备份）。</summary>
    /// <param name="today">今天。</param>
    void RunIfDue(DateOnly today);

    /// <summary>立即备份一次（不清理）。</summary>
    /// <returns>备份文件路径；失败返回 null。</returns>
    string? BackupNow();
}

/// <summary>测速阶段。</summary>
public enum SpeedPhase
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>探测节点。</summary>
    Probing,

    /// <summary>测延迟。</summary>
    Latency,

    /// <summary>测下载。</summary>
    Download,

    /// <summary>测上传。</summary>
    Upload,

    /// <summary>完成。</summary>
    Done,

    /// <summary>失败。</summary>
    Failed
}

/// <summary>
/// 网络测速：延迟 + 下载/上传带宽（下载走国内镜像，上传走 Cloudflare）。
/// </summary>
public interface ISpeedTestService
{
    /// <summary>是否正在测速。</summary>
    bool IsTesting { get; }

    /// <summary>当前阶段。</summary>
    SpeedPhase Phase { get; }

    /// <summary>当前下载节点显示名。</summary>
    string NodeName { get; }

    /// <summary>延迟（毫秒）。</summary>
    int LatencyMs { get; }

    /// <summary>下载带宽（Mbps）。</summary>
    double DownloadMbps { get; }

    /// <summary>上传带宽（Mbps）。</summary>
    double UploadMbps { get; }

    /// <summary>当前阶段文案。</summary>
    string StageText { get; }

    /// <summary>进度变化事件（阶段/数值更新）。</summary>
    event EventHandler? ProgressChanged;

    /// <summary>测速完成事件。</summary>
    event EventHandler? Completed;

    /// <summary>开始测速。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 倒计时服务：单次倒计时（不循环），到点提醒。
/// </summary>
public interface ICountdownService
{
    /// <summary>是否计时中。</summary>
    bool IsRunning { get; }

    /// <summary>剩余时长。</summary>
    TimeSpan Remaining { get; }

    /// <summary>本次总时长（分钟）。</summary>
    int TotalMinutes { get; }

    /// <summary>状态或剩余时间变化（每秒）。</summary>
    event EventHandler? StateChanged;

    /// <summary>开始倒计时。</summary>
    /// <param name="minutes">分钟数（1–600）。</param>
    void Start(int minutes);

    /// <summary>停止。</summary>
    void Stop();
}

/// <summary>
/// 网络状态监测：基于网络接口可用性 + TCP 探测。
/// </summary>
public interface INetworkMonitor
{
    /// <summary>是否在线。</summary>
    bool IsOnline { get; }

    /// <summary>最近一次探测延迟（毫秒，失败为 -1）。</summary>
    int LatencyMs { get; }

    /// <summary>状态文案（在线 · 32ms / 离线）。</summary>
    string StatusText { get; }

    /// <summary>状态变化事件。</summary>
    event EventHandler? StatusChanged;

    /// <summary>开始监测。</summary>
    void Start();

    /// <summary>停止监测。</summary>
    void Stop();
}

/// <summary>
/// 天气服务：Open-Meteo 免费接口（无需 Key），按设置的经纬度取当前天气与三日预报。
/// </summary>
public interface IWeatherService
{
    /// <summary>最近一次成功的天气快照；从未成功则为 null。</summary>
    WeatherSnapshot? Current { get; }

    /// <summary>数据更新事件（成功后触发）。</summary>
    event EventHandler? Updated;

    /// <summary>立即刷新（网络失败时保留旧数据）。</summary>
    Task RefreshAsync();

    /// <summary>按 IP 自动定位（更新城市/经纬度）并刷新天气。</summary>
    Task LocateAsync();

    /// <summary>启动定时刷新（30 分钟一次）。</summary>
    void Start();

    /// <summary>停止定时刷新。</summary>
    void Stop();
}

/// <summary>
/// 剪贴板历史：轮询剪贴板文本，保留最近 N 条并持久化到本地 JSON。
/// </summary>
public interface IClipboardHistoryService
{
    /// <summary>历史条目（新→旧）。</summary>
    IReadOnlyList<ClipboardEntry> Entries { get; }

    /// <summary>历史变化事件。</summary>
    event EventHandler? Changed;

    /// <summary>开始监听。</summary>
    void Start();

    /// <summary>停止监听。</summary>
    void Stop();

    /// <summary>清空历史。</summary>
    void Clear();

    /// <summary>删除单条。</summary>
    /// <param name="id">条目 Id。</param>
    void Remove(Guid id);
}

/// <summary>剪贴板历史条目。</summary>
/// <param name="Id">唯一标识。</param>
/// <param name="Text">文本内容。</param>
/// <param name="CapturedAt">复制时间。</param>
public sealed record ClipboardEntry(Guid Id, string Text, DateTimeOffset CapturedAt);

/// <summary>天气快照。</summary>
/// <param name="City">城市名。</param>
/// <param name="Temperature">当前温度（摄氏度）。</param>
/// <param name="Description">天气描述。</param>
/// <param name="IconGlyph">Segoe MDL2 图标字符。</param>
/// <param name="UpdatedAt">更新时间。</param>
/// <param name="Days">未来三天预报。</param>
public sealed record WeatherSnapshot(
    string City,
    double Temperature,
    string Description,
    string IconGlyph,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DailyForecast> Days);

/// <summary>单日预报。</summary>
/// <param name="Date">日期。</param>
/// <param name="Min">最低温。</param>
/// <param name="Max">最高温。</param>
/// <param name="Description">天气描述。</param>
/// <param name="IconGlyph">图标字符。</param>
public sealed record DailyForecast(DateOnly Date, double Min, double Max, string Description, string IconGlyph);

public interface IAutoStartService
{
    /// <summary>自启是否已启用。</summary>
    /// <returns>true 表示已启用。</returns>
    bool IsEnabled();

    /// <summary>启用或禁用自启。</summary>
    /// <param name="enabled">是否启用。</param>
    void SetEnabled(bool enabled);
}

/// <summary>
/// 全局热键动作目标：由 App 实现，把热键映射到业务命令与桌宠显隐。
/// </summary>
public interface IHotkeyActions
{
    /// <summary>开始工作。</summary>
    void StartWork();

    /// <summary>开始休息。</summary>
    void StartRest();

    /// <summary>结束当天。</summary>
    void EndDay();

    /// <summary>生成工作汇报。</summary>
    void GenerateReport();

    /// <summary>显示/隐藏桌宠。</summary>
    void TogglePet();

    /// <summary>区域截图。</summary>
    void Screenshot();

    /// <summary>开始/停止专注。</summary>
    void FocusToggle();

    /// <summary>显示/隐藏桌宠（灵动岛）。</summary>
    void ToggleVisible();
}

/// <summary>
/// 全局热键服务：使用系统热键注册，命中组合键时回调 <see cref="IHotkeyActions"/>。
/// </summary>
public interface IHotkeyService
{
    /// <summary>在 UI 线程注册全局热键并绑定动作目标。</summary>
    /// <param name="actions">动作目标（回调到业务命令）。</param>
    void Install(IHotkeyActions actions);

    /// <summary>注销全局热键（退出时调用）。</summary>
    void Uninstall();
}
