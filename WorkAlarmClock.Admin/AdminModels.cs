using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkAlarmClock.Admin;

/// <summary>
/// 主状态机状态（整数映射与主项目 Models.Enums.WorkState 完全一致，避免引用 WPF 主项目）。
/// </summary>
public enum WorkState
{
    /// <summary>待上班（不计工作也不计休息）。</summary>
    BeforeWork = 0,

    /// <summary>工作中。</summary>
    Working = 1,

    /// <summary>休息中（具体来源由触发来源推断）。</summary>
    Resting = 2,

    /// <summary>已下班。</summary>
    OffWork = 3
}

/// <summary>
/// 状态切换触发来源（整数映射与主项目 Models.Enums.TriggerSource 完全一致）。
/// </summary>
public enum TriggerSource
{
    /// <summary>作息表自动切换。</summary>
    Schedule = 0,

    /// <summary>用户手动切换。</summary>
    Manual = 1,

    /// <summary>锁屏检测。</summary>
    Lock = 2,

    /// <summary>空闲检测。</summary>
    Idle = 3,

    /// <summary>系统事件（启动落位 / 睡眠唤醒重落位）。</summary>
    System = 4
}

/// <summary>
/// 休息来源（不落库，由流水刷新时推断；映射与主项目 Models.Enums.RestSource 一致）。
/// </summary>
public enum RestSource
{
    /// <summary>午休（作息表）。</summary>
    Lunch = 0,

    /// <summary>锁屏离开。</summary>
    LockScreen = 1,

    /// <summary>键鼠空闲。</summary>
    Idle = 2,

    /// <summary>手动休息。</summary>
    Manual = 3
}

/// <summary>
/// 一条状态切换流水（对应 state_transitions 表的一行）。
/// </summary>
public sealed class TransitionRow
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }

    /// <summary>归属日期（yyyy-MM-dd 文本）。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>切换时刻（ISO8601 带时区，展示时转本地时间）。</summary>
    public DateTimeOffset Time { get; set; }

    /// <summary>切换前状态。</summary>
    public WorkState From { get; set; }

    /// <summary>切换后状态。</summary>
    public WorkState To { get; set; }

    /// <summary>触发来源。</summary>
    public TriggerSource Source { get; set; }

    /// <summary>备注（如 手动开始工作 / 午休 / 锁屏离开）。</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 今日事项（对应 work_tasks 表的一行）。
/// </summary>
public sealed class TaskRow
{
    /// <summary>事项唯一标识（GUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>添加时间戳。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>事项文本。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>标签（空串表示未分类）。</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// 日报生成记录（对应 report_records 表的一行）。
/// </summary>
public sealed class ReportRow
{
    /// <summary>生成时刻。</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>落盘文件路径。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>今日工作。</summary>
    public string TodayWork { get; set; } = string.Empty;

    /// <summary>明日计划。</summary>
    public string TomorrowPlan { get; set; } = string.Empty;

    /// <summary>风险与待办。</summary>
    public string Risks { get; set; } = string.Empty;
}

/// <summary>
/// 某一天的完整数据（day_record + 流水 + 事项 + 日报）。
/// </summary>
public sealed class DayDetail
{
    /// <summary>日期（yyyy-MM-dd）。</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>day_records 中是否存在该日记录。</summary>
    public bool HasDayRecord { get; set; }

    /// <summary>是否已手动结束当天。</summary>
    public bool ManualEnded { get; set; }

    /// <summary>当日流水（按时间升序）。</summary>
    public List<TransitionRow> Transitions { get; } = new List<TransitionRow>();

    /// <summary>当日事项。</summary>
    public List<TaskRow> Tasks { get; } = new List<TaskRow>();

    /// <summary>当日日报记录。</summary>
    public List<ReportRow> Reports { get; } = new List<ReportRow>();
}

/// <summary>
/// 单表行数。
/// </summary>
/// <param name="Table">表名。</param>
/// <param name="Rows">行数。</param>
public sealed record TableRowCount(string Table, long Rows);

/// <summary>
/// 数据库概览信息。
/// </summary>
public sealed class DatabaseOverview
{
    /// <summary>数据库完整路径。</summary>
    public string DbPath { get; set; } = string.Empty;

    /// <summary>数据库文件是否存在。</summary>
    public bool Exists { get; set; }

    /// <summary>数据库主文件字节数。</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>WAL 文件字节数（不存在为 0）。</summary>
    public long WalSizeBytes { get; set; }

    /// <summary>schema_version（取最大值）。</summary>
    public string SchemaVersion { get; set; } = "未知";

    /// <summary>各业务表行数。</summary>
    public List<TableRowCount> Tables { get; } = new List<TableRowCount>();

    /// <summary>最小日期。</summary>
    public string? MinDate { get; set; }

    /// <summary>最大日期。</summary>
    public string? MaxDate { get; set; }

    /// <summary>settings 表键列表。</summary>
    public List<string> SettingKeys { get; } = new List<string>();

    /// <summary>meta 表键值对。</summary>
    public List<KeyValuePair<string, string>> MetaRows { get; } = new List<KeyValuePair<string, string>>();
}

/// <summary>
/// 删除某天数据后的各表影响行数。
/// </summary>
/// <param name="DayRecords">day_records 删除行数。</param>
/// <param name="Transitions">state_transitions 删除行数。</param>
/// <param name="Tasks">work_tasks 删除行数。</param>
/// <param name="Reports">report_records 删除行数。</param>
/// <param name="ReminderStates">reminder_states 删除行数。</param>
public sealed record DeleteResult(int DayRecords, int Transitions, int Tasks, int Reports, int ReminderStates);

/// <summary>
/// 单日重放汇总（午休已包含在休息内）。
/// </summary>
/// <param name="Date">日期。</param>
/// <param name="TransitionCount">流水条数。</param>
/// <param name="Work">工作累计时长。</param>
/// <param name="Rest">休息累计时长。</param>
/// <param name="Lunch">其中午休时长。</param>
public sealed record RecentDayRow(DateOnly Date, int TransitionCount, TimeSpan Work, TimeSpan Rest, TimeSpan Lunch);

/// <summary>
/// settings 表的可编辑设置子集（只定义管理工具需要读写的字段）。
/// 主项目 AppSettings 的其余字段通过 <see cref="Other"/> 原样保留，写回时不丢失。
/// </summary>
public sealed class AppSettingsDto
{
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

    /// <summary>空闲时段是否计入工作时长（决定汇总口径）。</summary>
    public bool CountIdleAsWork { get; set; }

    /// <summary>未识别的其余设置项（原样保留，写回时不丢失）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; set; }

    /// <summary>创建与主项目一致的默认设置（工作日 = 周一至周五）。</summary>
    /// <returns>默认设置实例。</returns>
    public static AppSettingsDto CreateDefault()
    {
        AppSettingsDto settings = new AppSettingsDto();
        settings.Workdays[(int)DayOfWeek.Monday] = true;
        settings.Workdays[(int)DayOfWeek.Tuesday] = true;
        settings.Workdays[(int)DayOfWeek.Wednesday] = true;
        settings.Workdays[(int)DayOfWeek.Thursday] = true;
        settings.Workdays[(int)DayOfWeek.Friday] = true;
        return settings;
    }
}

/// <summary>
/// 管理工具的业务异常（统一转成友好错误信息，不打印堆栈）。
/// </summary>
public sealed class AdminException : Exception
{
    /// <summary>构造业务异常。</summary>
    /// <param name="message">错误信息。</param>
    public AdminException(string message)
        : base(message)
    {
    }
}
