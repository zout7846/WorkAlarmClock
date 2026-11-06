namespace WorkAlarmClock.Models;

/// <summary>
/// 主状态机状态。
/// </summary>
public enum WorkState
{
    /// <summary>待上班（不计工作也不计休息）。</summary>
    BeforeWork,

    /// <summary>工作中。</summary>
    Working,

    /// <summary>休息中（具体来源见 <see cref="RestSource"/>）。</summary>
    Resting,

    /// <summary>已下班。</summary>
    OffWork
}

/// <summary>
/// 状态切换的触发来源（持久化进流水）。
/// </summary>
public enum TriggerSource
{
    /// <summary>作息表自动切换。</summary>
    Schedule,

    /// <summary>用户手动切换（优先级最高）。</summary>
    Manual,

    /// <summary>锁屏检测。</summary>
    Lock,

    /// <summary>空闲检测。</summary>
    Idle,

    /// <summary>系统事件（启动落位 / 睡眠唤醒重落位）。</summary>
    System
}

/// <summary>
/// 休息的具体来源（气泡注明）。
/// </summary>
public enum RestSource
{
    /// <summary>午休（作息表）。</summary>
    Lunch,

    /// <summary>锁屏离开。</summary>
    LockScreen,

    /// <summary>键鼠空闲。</summary>
    Idle,

    /// <summary>手动休息。</summary>
    Manual
}

/// <summary>
/// 提醒类型。
/// </summary>
public enum ReminderKind
{
    /// <summary>弱提醒（下班前写日报）。</summary>
    Weak,

    /// <summary>强提醒（下班前打卡）。</summary>
    Strong
}

/// <summary>
/// 作息时间点类型。
/// </summary>
public enum SchedulePointKind
{
    /// <summary>上班时间。</summary>
    WorkStart,

    /// <summary>午休开始。</summary>
    LunchStart,

    /// <summary>午休结束。</summary>
    LunchEnd,

    /// <summary>下班时间。</summary>
    WorkEnd
}

/// <summary>
/// 桌宠气泡类型。
/// </summary>
public enum BubbleKind
{
    /// <summary>普通信息。</summary>
    Info,

    /// <summary>已恢复工作（可纠正）。</summary>
    RestResume,

    /// <summary>弱提醒。</summary>
    ReminderWeak,

    /// <summary>强提醒。</summary>
    ReminderStrong
}

/// <summary>
/// 提示音类型。
/// </summary>
public enum SoundKind
{
    /// <summary>弱提醒音。</summary>
    Weak,

    /// <summary>强提醒音。</summary>
    Strong,

    /// <summary>确认音。</summary>
    Confirm
}
