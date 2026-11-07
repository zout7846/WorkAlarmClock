using System.Globalization;

namespace WorkAlarmClock.Admin;

/// <summary>
/// 按主项目 ReplayCalculator / StatisticsService 的语义重放流水，计算工作/休息/午休时长。
/// 管理工具只做只读统计，不引用主项目（避免 WPF 依赖）。
/// </summary>
public static class SummaryCalculator
{
    /// <summary>历史某日的重放终点（本地时间 23:59:59，与主项目 EndOfDay 一致）。</summary>
    /// <param name="date">日期。</param>
    /// <returns>重放终点。</returns>
    public static DateTimeOffset EndOfDay(DateOnly date)
    {
        DateTime local = date.ToDateTime(new TimeOnly(23, 59, 59));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>
    /// 重放一天的流水，累计工作/休息/午休时长（午休包含在休息内）。
    /// </summary>
    /// <param name="transitions">当日流水（任意顺序，内部按时间升序）。</param>
    /// <param name="end">重放终点（今日用 DateTimeOffset.Now，历史日用 <see cref="EndOfDay"/>）。</param>
    /// <param name="settings">设置。</param>
    /// <returns>工作、休息、午休三段时长。</returns>
    public static (TimeSpan WorkTime, TimeSpan RestTime, TimeSpan LunchTime) Replay(
        IReadOnlyList<TransitionRow> transitions,
        DateTimeOffset end,
        AppSettingsDto settings)
    {
        TimeSpan work = TimeSpan.Zero;
        TimeSpan rest = TimeSpan.Zero;
        TimeSpan lunch = TimeSpan.Zero;
        if (transitions.Count == 0)
        {
            return (work, rest, lunch);
        }

        List<TransitionRow> ordered = transitions.OrderBy(row => row.Time).ToList();
        WorkState previousState = WorkState.BeforeWork;
        RestSource? previousRestSource = null;
        DateTimeOffset previousTime = ordered[0].Time;

        foreach (TransitionRow transition in ordered)
        {
            Accumulate(previousState, previousRestSource, transition.Time - previousTime, settings, ref work, ref rest, ref lunch);
            previousState = transition.To;
            previousTime = transition.Time;
            previousRestSource = transition.To == WorkState.Resting
                ? AdminFormat.ResolveRestSource(transition, settings)
                : null;
        }

        Accumulate(previousState, previousRestSource, end - previousTime, settings, ref work, ref rest, ref lunch);
        return (work, rest, lunch);
    }

    /// <summary>计算最近 N 天（含今天）的每日汇总，仅返回有流水的日期。</summary>
    /// <param name="transitions">日期区间内的全部流水。</param>
    /// <param name="today">今天。</param>
    /// <param name="days">统计天数。</param>
    /// <param name="settings">设置。</param>
    /// <returns>按日期升序的每日汇总。</returns>
    public static List<RecentDayRow> SummarizeRecentDays(
        IReadOnlyList<TransitionRow> transitions,
        DateOnly today,
        int days,
        AppSettingsDto settings)
    {
        int count = Math.Clamp(days, 1, 366);
        DateOnly from = today.AddDays(-(count - 1));
        Dictionary<string, List<TransitionRow>> byDate = new Dictionary<string, List<TransitionRow>>(StringComparer.Ordinal);
        foreach (TransitionRow row in transitions)
        {
            if (!byDate.TryGetValue(row.Date, out List<TransitionRow>? rows))
            {
                rows = new List<TransitionRow>();
                byDate[row.Date] = rows;
            }

            rows.Add(row);
        }

        List<RecentDayRow> result = new List<RecentDayRow>();
        DateTimeOffset now = DateTimeOffset.Now;
        for (DateOnly date = from; date <= today; date = date.AddDays(1))
        {
            string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!byDate.TryGetValue(key, out List<TransitionRow>? dayRows) || dayRows.Count == 0)
            {
                continue;
            }

            DateTimeOffset end = date == DateOnly.FromDateTime(now.LocalDateTime) ? now : EndOfDay(date);
            (TimeSpan work, TimeSpan rest, TimeSpan lunch) = Replay(dayRows, end, settings);
            result.Add(new RecentDayRow(date, dayRows.Count, work, rest, lunch));
        }

        return result;
    }

    private static void Accumulate(
        WorkState state,
        RestSource? restSource,
        TimeSpan span,
        AppSettingsDto settings,
        ref TimeSpan work,
        ref TimeSpan rest,
        ref TimeSpan lunch)
    {
        if (span <= TimeSpan.Zero)
        {
            return;
        }

        if (state == WorkState.Working)
        {
            work += span;
            return;
        }

        if (state != WorkState.Resting)
        {
            return;
        }

        if (settings.CountIdleAsWork && restSource == RestSource.Idle)
        {
            work += span;
        }
        else
        {
            rest += span;
        }

        if (restSource == RestSource.Lunch)
        {
            lunch += span;
        }
    }
}

