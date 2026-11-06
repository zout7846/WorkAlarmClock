using System;
using System.Collections.Generic;
using System.Globalization;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 作息服务：工作日判定、期望状态推算、时间点与下一切换点。
/// </summary>
public sealed class ScheduleService : IScheduleService
{
    /// <summary>向后查找下一切换点的最大天数（覆盖长假后恢复）。</summary>
    private const int MaxForwardSearchDays = 30;

    private readonly ISettingsService _settings;

    /// <summary>构造作息服务。</summary>
    /// <param name="settings">设置服务（每次读取最新值，设置即时生效）。</param>
    public ScheduleService(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public bool IsWorkday(DateOnly date)
    {
        bool[] workdays = _settings.Current.Workdays;
        int index = (int)date.DayOfWeek;
        if (index < 0 || index >= workdays.Length)
        {
            return false;
        }

        return workdays[index];
    }

    /// <inheritdoc />
    public WorkState GetExpectedState(DateTimeOffset now)
    {
        (DateOnly date, TimeOnly time) = Split(now);
        AppSettings settings = _settings.Current;

        if (!IsWorkday(date))
        {
            return WorkState.BeforeWork;
        }

        if (time < settings.WorkStartTime)
        {
            return WorkState.BeforeWork;
        }

        if (time >= settings.WorkEndTime)
        {
            return WorkState.OffWork;
        }

        if (settings.LunchEnabled && time >= settings.LunchStartTime && time < settings.LunchEndTime)
        {
            return WorkState.Resting;
        }

        return WorkState.Working;
    }

    /// <inheritdoc />
    public IReadOnlyList<SchedulePoint> GetTodayPoints(DateOnly date)
    {
        List<SchedulePoint> points = new List<SchedulePoint>();
        if (!IsWorkday(date))
        {
            return points;
        }

        AppSettings settings = _settings.Current;
        points.Add(new SchedulePoint(settings.WorkStartTime, SchedulePointKind.WorkStart));
        if (settings.LunchEnabled)
        {
            points.Add(new SchedulePoint(settings.LunchStartTime, SchedulePointKind.LunchStart));
            points.Add(new SchedulePoint(settings.LunchEndTime, SchedulePointKind.LunchEnd));
        }

        points.Add(new SchedulePoint(settings.WorkEndTime, SchedulePointKind.WorkEnd));
        points.Sort((left, right) => left.Time.CompareTo(right.Time));
        return points;
    }

    /// <inheritdoc />
    public DateTimeOffset? NextTransitionAt(DateTimeOffset now)
    {
        (DateOnly date, TimeOnly time) = Split(now);
        for (int offset = 0; offset <= MaxForwardSearchDays; offset++)
        {
            DateOnly candidate = date.AddDays(offset);
            foreach (SchedulePoint point in GetTodayPoints(candidate))
            {
                if (offset == 0 && point.Time <= time)
                {
                    continue;
                }

                return ToLocal(candidate, point.Time);
            }
        }

        return null;
    }

    /// <summary>把日期与日内时刻组合为本地 DateTimeOffset。</summary>
    /// <param name="date">日期。</param>
    /// <param name="time">日内时刻。</param>
    /// <returns>本地时刻。</returns>
    public static DateTimeOffset ToLocal(DateOnly date, TimeOnly time)
    {
        DateTime local = date.ToDateTime(time);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>把时刻拆分为本地日期与日内时刻。</summary>
    /// <param name="now">时刻。</param>
    /// <returns>本地日期与时刻。</returns>
    public static (DateOnly Date, TimeOnly Time) Split(DateTimeOffset now)
    {
        DateTime local = now.LocalDateTime;
        return (DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }

    /// <summary>状态中文文案。</summary>
    /// <param name="state">状态。</param>
    /// <param name="restSource">休息来源。</param>
    /// <returns>中文状态名。</returns>
    public static string StateToText(WorkState state, RestSource? restSource)
    {
        return state switch
        {
            WorkState.Working => "工作中",
            WorkState.Resting => restSource switch
            {
                RestSource.Lunch => "午休中",
                RestSource.LockScreen => "锁屏休息",
                RestSource.Idle => "空闲休息",
                RestSource.Manual => "休息中",
                _ => "休息中"
            },
            WorkState.OffWork => "已下班",
            _ => "待上班"
        };
    }

    /// <summary>作息时间点中文文案。</summary>
    /// <param name="kind">时间点类型。</param>
    /// <returns>中文名称。</returns>
    public static string PointKindToText(SchedulePointKind kind)
    {
        return kind switch
        {
            SchedulePointKind.WorkStart => "上班",
            SchedulePointKind.LunchStart => "午休开始",
            SchedulePointKind.LunchEnd => "午休结束",
            _ => "下班"
        };
    }

    /// <summary>把日期格式化为 ISO 字符串（日志用）。</summary>
    /// <param name="date">日期。</param>
    /// <returns>yyyy-MM-dd 文本。</returns>
    public static string DateToText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
