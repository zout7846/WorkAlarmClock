//using System;

namespace WorkAlarmClock.Models;

/// <summary>
/// 应用设置（settings.json 反序列化目标）。
/// </summary>
public sealed class AppSettings
{
    // ========== 作息 ==========

    /// <summary>上班时间。</summary>
    public TimeOnly WorkStartTime { get; set; } = new TimeOnly(9, 0);

    /// <summary>下班时间。</summary>
    public TimeOnly WorkEndTime { get; set; } = new TimeOnly(18, 0);

    /// <summary>是否启用午休。</summary>
    public bool LunchEnabled { get; set; } = true;

    /// <summary>午休开始时间。</summary>
    public TimeOnly LunchStartTime { get; set; } = new TimeOnly(12, 0);

    /// <summary>午休结束时间。</summary>
    public TimeOnly LunchEndTime { get; set; } = new TimeOnly(13, 0);

    /// <summary>周日..周六 共 7 个布尔（下标 = <see cref="DayOfWeek"/> 枚举值）。</summary>
    public bool[] Workdays { get; set; } = new bool[7];

    // ========== 提醒 ==========

    /// <summary>是否启用弱提醒（写日报）。</summary>
    public bool WeakReminderEnabled { get; set; } = true;

    /// <summary>弱提醒提前量（分钟）。</summary>
    public int WeakReminderLeadMinutes { get; set; } = 30;

    /// <summary>是否启用强提醒（打卡）。</summary>
    public bool StrongReminderEnabled { get; set; } = true;

    /// <summary>强提醒提前量（分钟）。</summary>
    public int StrongReminderLeadMinutes { get; set; } = 5;

    /// <summary>强提醒是否允许忽略。</summary>
    public bool StrongAllowIgnore { get; set; }

    /// <summary>强提醒未确认时的重复间隔（秒）。</summary>
    public int StrongRepeatIntervalSeconds { get; set; } = 60;

    /// <summary>强提醒未确认时的最大重复次数。</summary>
    public int StrongRepeatMaxCount { get; set; } = 3;

    /// <summary>是否开启提示音。</summary>
    public bool SoundEnabled { get; set; } = true;

    // ========== 检测 ==========

    /// <summary>空闲判定阈值（分钟）。</summary>
    public int IdleThresholdMinutes { get; set; } = 5;

    /// <summary>锁屏即休息。</summary>
    public bool LockAsRest { get; set; } = true;

    /// <summary>空闲时段是否计入工作时长（锁屏段始终计入休息）。</summary>
    public bool CountIdleAsWork { get; set; }

    // ========== 外观 / 存储 ==========

    /// <summary>桌宠不透明度（0.2 – 1.0）。</summary>
    public double PetOpacity { get; set; } = 0.8;

    /// <summary>日报目录；空字符串表示使用默认目录。</summary>
    public string ReportDirectory { get; set; } = string.Empty;

    /// <summary>日报页脚（可选单行文本，默认空）；渲染在纯文本日报末尾。</summary>
    public string ReportFooter { get; set; } = string.Empty;

    /// <summary>开机自启（注册表 Run 键）。</summary>
    public bool AutoStart { get; set; }

    // ========== 健康提醒 ==========

    /// <summary>久坐提醒开关。</summary>
    public bool HealthSitEnabled { get; set; } = true;

    /// <summary>久坐提醒间隔（分钟）。</summary>
    public int HealthSitIntervalMinutes { get; set; } = 60;

    /// <summary>喝水提醒开关。</summary>
    public bool HealthDrinkEnabled { get; set; } = true;

    /// <summary>喝水提醒间隔（分钟）。</summary>
    public int HealthDrinkIntervalMinutes { get; set; } = 90;

    // ========== 番茄钟 ==========

    /// <summary>专注时长（分钟）。</summary>
    public int PomodoroFocusMinutes { get; set; } = 25;

    /// <summary>短休息时长（分钟）。</summary>
    public int PomodoroBreakMinutes { get; set; } = 5;

    /// <summary>长休息时长（分钟）。</summary>
    public int PomodoroLongBreakMinutes { get; set; } = 15;

    /// <summary>每几轮进入一次长休息。</summary>
    public int PomodoroRounds { get; set; } = 4;

    /// <summary>专注结束后自动开始休息。</summary>
    public bool PomodoroAutoBreak { get; set; } = true;

    // ========== 报表模板 ==========

    /// <summary>
    /// 日报模板（支持占位符 {date} {todayWork} {tomorrowPlan} {risks} {work} {rest}）；
    /// 空字符串表示使用内置默认模板。
    /// </summary>
    public string ReportTemplate { get; set; } = string.Empty;

    /// <summary>日报落盘格式：0=仅 txt，1=仅 md，2=两者（默认）。</summary>
    public int ReportFileFormat { get; set; } = 2;

    // ========== 备份 ==========

    /// <summary>每日自动备份数据库。</summary>
    public bool BackupEnabled { get; set; } = true;

    /// <summary>备份保留天数。</summary>
    public int BackupKeepDays { get; set; } = 30;

    // ========== 桌宠养成 ==========

    /// <summary>桌宠缩放比例（0.8 – 1.5）。</summary>
    public double PetScale { get; set; } = 1.0;

    /// <summary>桌宠皮肤：0=蓝猫 1=粉兔 2=薄荷 3=暖橘。</summary>
    public int PetSkin { get; set; }

    /// <summary>桌宠形象：0=小猫 1=小狗 2=狐狸。</summary>
    public int PetSpecies { get; set; }

    /// <summary>自定义桌宠图片路径（空 = 使用内置 pet.png）。</summary>
    public string PetImagePath { get; set; } = string.Empty;

    /// <summary>拖拽桌宠时自动吸附屏幕边缘。</summary>
    public bool SnapToEdges { get; set; } = true;

    // ========== 倒计时 ==========

    /// <summary>上次使用的倒计时分钟数。</summary>
    public int CountdownMinutes { get; set; } = 10;

    // ========== 剪贴板历史 ==========

    /// <summary>记录剪贴板历史。</summary>
    public bool ClipboardHistoryEnabled { get; set; } = true;

    /// <summary>剪贴板历史保留条数。</summary>
    public int ClipboardHistoryMax { get; set; } = 50;

    // ========== 网络与天气 ==========

    /// <summary>网络状态监测开关。</summary>
    public bool NetworkMonitorEnabled { get; set; } = true;

    /// <summary>天气开关。</summary>
    public bool WeatherEnabled { get; set; } = true;

    /// <summary>启动时按 IP 自动定位（失败时回退到手动坐标）。</summary>
    public bool WeatherAutoLocate { get; set; } = true;

    /// <summary>手动指定地区：为 true 时忽略 IP 定位，使用下方城市与经纬度。</summary>
    public bool WeatherManualLocation { get; set; }

    /// <summary>天气城市名（仅展示；自动定位会覆盖）。</summary>
    public string WeatherCity { get; set; } = string.Empty;

    /// <summary>天气纬度。</summary>
    public double WeatherLatitude { get; set; } = 39.9042;

    /// <summary>天气经度。</summary>
    public double WeatherLongitude { get; set; } = 116.4074;

    /// <summary>上次成功按 IP 定位的时间（用于避免启动时重复联网定位）。</summary>
    public DateTimeOffset? WeatherLocatedAt { get; set; }

    /// <summary>桌宠亲密度（点击互动累积）。</summary>
    public int PetIntimacy { get; set; }

    // ========== 截图 ==========

    /// <summary>截图保存目录；空字符串表示使用默认目录（图片\WorkAlarmClock截图）。</summary>
    public string ScreenshotDirectory { get; set; } = string.Empty;

    /// <summary>截图后是否自动复制到剪贴板。</summary>
    public bool ScreenshotCopyToClipboard { get; set; } = true;

    // ========== 热键 ==========

    /// <summary>全局热键组合（5 个动作）；默认 Ctrl+Alt+W/R/E/G/S。</summary>
    public HotkeySettings Hotkeys { get; set; } = new HotkeySettings();

    // ========== 桌宠位置记忆 ==========

    /// <summary>桌宠左上角 X（NaN 表示未记忆）。</summary>
    public double PetLeft { get; set; } = double.NaN;

    /// <summary>桌宠左上角 Y（NaN 表示未记忆）。</summary>
    public double PetTop { get; set; } = double.NaN;

    /// <summary>桌宠是否处于贴边收缩态。</summary>
    public bool PetCollapsed { get; set; }

    /// <summary>创建带默认值的设置（工作日 = 周一至周五；热键与页脚取默认）。</summary>
    /// <returns>默认设置实例。</returns>
    public static AppSettings CreateDefault()
    {
        AppSettings settings = new AppSettings();
        settings.Workdays[(int)DayOfWeek.Monday] = true;
        settings.Workdays[(int)DayOfWeek.Tuesday] = true;
        settings.Workdays[(int)DayOfWeek.Wednesday] = true;
        settings.Workdays[(int)DayOfWeek.Thursday] = true;
        settings.Workdays[(int)DayOfWeek.Friday] = true;
        settings.Hotkeys = new HotkeySettings();
        return settings;
    }

    /// <summary>创建设置副本（避免草稿对象与当前对象共享引用）。</summary>
    /// <returns>副本实例。</returns>
    public AppSettings Clone()
    {
        return new AppSettings
        {
            WorkStartTime = WorkStartTime,
            WorkEndTime = WorkEndTime,
            LunchEnabled = LunchEnabled,
            LunchStartTime = LunchStartTime,
            LunchEndTime = LunchEndTime,
            Workdays = (bool[])Workdays.Clone(),
            WeakReminderEnabled = WeakReminderEnabled,
            WeakReminderLeadMinutes = WeakReminderLeadMinutes,
            StrongReminderEnabled = StrongReminderEnabled,
            StrongReminderLeadMinutes = StrongReminderLeadMinutes,
            StrongAllowIgnore = StrongAllowIgnore,
            StrongRepeatIntervalSeconds = StrongRepeatIntervalSeconds,
            StrongRepeatMaxCount = StrongRepeatMaxCount,
            SoundEnabled = SoundEnabled,
            IdleThresholdMinutes = IdleThresholdMinutes,
            LockAsRest = LockAsRest,
            CountIdleAsWork = CountIdleAsWork,
            PetOpacity = PetOpacity,
            ReportDirectory = ReportDirectory,
            ReportFooter = ReportFooter,
            AutoStart = AutoStart,
            HealthSitEnabled = HealthSitEnabled,
            HealthSitIntervalMinutes = HealthSitIntervalMinutes,
            HealthDrinkEnabled = HealthDrinkEnabled,
            HealthDrinkIntervalMinutes = HealthDrinkIntervalMinutes,
            PomodoroFocusMinutes = PomodoroFocusMinutes,
            PomodoroBreakMinutes = PomodoroBreakMinutes,
            PomodoroLongBreakMinutes = PomodoroLongBreakMinutes,
            PomodoroRounds = PomodoroRounds,
            PomodoroAutoBreak = PomodoroAutoBreak,
            ReportTemplate = ReportTemplate,
            ReportFileFormat = ReportFileFormat,
            BackupEnabled = BackupEnabled,
            BackupKeepDays = BackupKeepDays,
            PetScale = PetScale,
            PetSkin = PetSkin,
            PetSpecies = PetSpecies,
            PetImagePath = PetImagePath,
            SnapToEdges = SnapToEdges,
            CountdownMinutes = CountdownMinutes,
            ClipboardHistoryEnabled = ClipboardHistoryEnabled,
            ClipboardHistoryMax = ClipboardHistoryMax,
            NetworkMonitorEnabled = NetworkMonitorEnabled,
            WeatherEnabled = WeatherEnabled,
            WeatherAutoLocate = WeatherAutoLocate,
            WeatherManualLocation = WeatherManualLocation,
            WeatherCity = WeatherCity,
            WeatherLatitude = WeatherLatitude,
            WeatherLongitude = WeatherLongitude,
            WeatherLocatedAt = WeatherLocatedAt,
            PetIntimacy = PetIntimacy,
            ScreenshotDirectory = ScreenshotDirectory,
            ScreenshotCopyToClipboard = ScreenshotCopyToClipboard,
            Hotkeys = new HotkeySettings
            {
                StartWork = Hotkeys.StartWork,
                StartRest = Hotkeys.StartRest,
                EndDay = Hotkeys.EndDay,
                GenerateReport = Hotkeys.GenerateReport,
                TogglePet = Hotkeys.TogglePet,
                Screenshot = Hotkeys.Screenshot,
                Focus = Hotkeys.Focus,
                ToggleVisible = Hotkeys.ToggleVisible,
                VariableNameGenerator = Hotkeys.VariableNameGenerator
            },
            PetLeft = PetLeft,
            PetTop = PetTop,
            PetCollapsed = PetCollapsed
        };
    }
}
