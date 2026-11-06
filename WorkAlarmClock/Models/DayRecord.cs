using System;
using System.Collections.Generic;

namespace WorkAlarmClock.Models;

/// <summary>
/// 一条状态切换流水（只追加，不修改）。
/// </summary>
public sealed class StateTransition
{
    /// <summary>切换时刻（本地时间）。</summary>
    public DateTimeOffset Time { get; set; }

    /// <summary>切换前状态。</summary>
    public WorkState From { get; set; }

    /// <summary>切换后状态。</summary>
    public WorkState To { get; set; }

    /// <summary>触发来源。</summary>
    public TriggerSource Source { get; set; }

    /// <summary>备注，例如 "午休" / "锁屏" / "空闲"。</summary>
    public string? Note { get; set; }
}

/// <summary>
/// 今日事项条目。
/// </summary>
public sealed class WorkTaskItem
{
    /// <summary>唯一标识。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>添加时间戳（展示精度为分钟）。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>事项文本。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>标签（如 开发/会议/文档），空串表示未分类。</summary>
    public string Tag { get; set; } = string.Empty;
}

/// <summary>
/// 一次日报生成记录。
/// </summary>
public sealed class ReportRecord
{
    /// <summary>生成时刻。</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>落盘文件路径。</summary>
    public string FilePath { get; set; } = string.Empty;
    ///<summary>今日工作</summary>
    public string TodayWork { get; set; } = "-";

    /// <summary>明日计划。</summary>
    public string TomorrowPlan { get; set; } = "-";

    /// <summary>风险与待办。</summary>
    public string Risks { get; set; } = "-";
}

/// <summary>
/// 每日数据（data/YYYY-MM-DD.json 反序列化目标）。
/// </summary>
public sealed class DayRecord
{
    /// <summary>日期键。</summary>
    public DateOnly Date { get; set; }

    /// <summary>状态切换流水（只追加）。</summary>
    public List<StateTransition> Transitions { get; set; } = new List<StateTransition>();

    /// <summary>今日事项列表。</summary>
    public List<WorkTaskItem> Tasks { get; set; } = new List<WorkTaskItem>();

    /// <summary>日报生成记录。</summary>
    public List<ReportRecord> Reports { get; set; } = new List<ReportRecord>();

    /// <summary>手动"结束当天"终态锁，跨天清除。</summary>
    public bool ManualEnded { get; set; }
}

/// <summary>
/// 当日提醒进度（内存态，跨天重置）。
/// </summary>
public sealed class ReminderState
{
    /// <summary>弱提醒当日是否已触发过。</summary>
    public bool WeakFired { get; set; }

    /// <summary>弱提醒"稍后提醒"生效时刻。</summary>
    public DateTimeOffset? WeakDeferredUntil { get; set; }

    /// <summary>弱提醒当日是否被忽略。</summary>
    public bool WeakIgnored { get; set; }

    /// <summary>强提醒当日是否已触发过。</summary>
    public bool StrongFired { get; set; }

    /// <summary>强提醒当日是否已确认打卡。</summary>
    public bool StrongConfirmed { get; set; }

    /// <summary>强提醒已重复次数。</summary>
    public int StrongRepeatCount { get; set; }

    /// <summary>重置为"新的一天"。</summary>
    public void ResetForNewDay()
    {
        WeakFired = false;
        WeakDeferredUntil = null;
        WeakIgnored = false;
        StrongFired = false;
        StrongConfirmed = false;
        StrongRepeatCount = 0;
    }
}
