using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 统计服务：今日与本周（工作日）工作/休息时长聚合。
/// 统计全部由每日流水重放得出，与当日记录天然一致。
/// </summary>
public sealed class StatisticsService : IStatisticsService
{
    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;
    private readonly ITimeTrackingService _timeTracking;
    private readonly IScheduleService _schedule;

    /// <summary>构造统计服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="storage">存储服务。</param>
    /// <param name="timeTracking">工时统计服务。</param>
    /// <param name="schedule">作息服务。</param>
    public StatisticsService(
        ISettingsService settings,
        IStorageService storage,
        ITimeTrackingService timeTracking,
        IScheduleService schedule)
    {
        _settings = settings;
        _storage = storage;
        _timeTracking = timeTracking;
        _schedule = schedule;
    }

    /// <inheritdoc />
    public WeekStats GetStats()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);

        TimeSpan todayWork = _timeTracking.WorkDuration;
        TimeSpan todayRest = _timeTracking.RestDuration;

        TimeSpan weekWork = TimeSpan.Zero;
        TimeSpan weekRest = TimeSpan.Zero;
        bool todayExistsInFiles = false;

        IReadOnlyList<DayRecord> week = _storage.LoadWeek(today);
        foreach (DayRecord record in week)
        {
            if (record.Date == today)
            {
                todayExistsInFiles = true;
                continue;
            }

            if (!_schedule.IsWorkday(record.Date))
            {
                continue;
            }

            ReplayResult result = ReplayCalculator.Replay(record.Transitions, EndOfDay(record.Date), _settings.Current);
            weekWork += result.Work;
            weekRest += result.Rest;
        }

        // 今日使用内存中的实时值（可能尚未落盘）。
        weekWork += todayWork;
        weekRest += todayRest;
        if (!todayExistsInFiles)
        {
            AppEnvironment.Log("今日记录尚未落盘，周统计使用内存实时值。");
        }

        return new WeekStats(todayWork, todayRest, weekWork, weekRest);
    }

    /// <summary>取当日 23:59:59 作为重放终点，闭合历史日期的开放段。</summary>
    private static DateTimeOffset EndOfDay(DateOnly date)
    {
        DateTime local = date.ToDateTime(new TimeOnly(23, 59, 59));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <inheritdoc />
    public IReadOnlyList<DayStat> GetRecentDays(int days)
    {
        int count = Math.Clamp(days, 1, 31);
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        List<DayStat> list = new List<DayStat>(count);

        for (int offset = count - 1; offset >= 0; offset--)
        {
            DateOnly date = today.AddDays(-offset);
            bool isWorkday = _schedule.IsWorkday(date);

            if (date == today)
            {
                // 今日用内存实时值，避免尚未落盘时统计为 0。
                list.Add(new DayStat(date, _timeTracking.WorkDuration, _timeTracking.RestDuration, isWorkday));
                continue;
            }

            DayRecord? record = _storage.LoadDay(date);
            if (record is null || record.Transitions.Count == 0)
            {
                list.Add(new DayStat(date, TimeSpan.Zero, TimeSpan.Zero, isWorkday));
                continue;
            }

            ReplayResult result = ReplayCalculator.Replay(record.Transitions, EndOfDay(date), _settings.Current);
            list.Add(new DayStat(date, result.Work, result.Rest, isWorkday));
        }

        return list;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimelineSlice> GetTodayTimeline()
    {
        IReadOnlyList<StateTransition> transitions = _timeTracking.Today.Transitions;
        if (transitions is null || transitions.Count == 0)
        {
            return Array.Empty<TimelineSlice>();
        }

        List<StateTransition> ordered = transitions.OrderBy(t => t.Time).ToList();
        List<TimelineSlice> slices = new List<TimelineSlice>();
        DateTimeOffset now = DateTimeOffset.Now;

        for (int i = 0; i < ordered.Count; i++)
        {
            DateTimeOffset start = ordered[i].Time;
            DateTimeOffset end = i + 1 < ordered.Count ? ordered[i + 1].Time : now;
            if (end <= start)
            {
                continue;
            }

            string kind = ResolveKind(ordered[i]);
            if (slices.Count > 0 && slices[^1].Kind == kind)
            {
                // 相邻同色段合并，避免时间轴碎成小方块。
                TimelineSlice last = slices[^1];
                slices[^1] = last with { End = end, Label = BuildLabel(kind, last.Start, end) };
                continue;
            }

            slices.Add(new TimelineSlice(start, end, kind, BuildLabel(kind, start, end)));
        }

        return slices;
    }

    /// <summary>把状态切换映射为时间轴颜色类型。</summary>
    private string ResolveKind(StateTransition transition) => transition.To switch
    {
        WorkState.Working => "work",
        WorkState.OffWork => "off",
        _ => ReplayCalculator.ResolveRestSource(transition, _settings.Current) == RestSource.Lunch ? "lunch" : "rest"
    };

    /// <summary>生成时间轴段落文案：类型 + 起止时间 + 时长。</summary>
    private static string BuildLabel(string kind, DateTimeOffset start, DateTimeOffset end)
    {
        string name = kind switch
        {
            "work" => "工作",
            "lunch" => "午休",
            "off" => "已下班",
            _ => "休息"
        };

        TimeSpan span = end - start;
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} {1:HH:mm}–{2:HH:mm}（{3:0.#} 分）",
            name,
            start.LocalDateTime,
            end.LocalDateTime,
            Math.Round(span.TotalMinutes, 0));
    }
}

/// <summary>
/// 开机自启服务：读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// </summary>
public sealed class AutoStartService : IAutoStartService
{
    /// <summary>注册表 Run 键路径。</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启项名称。</summary>
    private const string ValueName = "WorkAlarmClock";

    /// <inheritdoc />
    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            object? value = key?.GetValue(ValueName);
            return value is string text && text.Length > 0;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("读取开机自启状态失败。", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                AppEnvironment.LogWarning("无法打开注册表 Run 键，开机自启设置未生效。");
                return;
            }

            if (enabled)
            {
                string executable = Environment.ProcessPath ?? string.Empty;
                if (string.IsNullOrEmpty(executable))
                {
                    AppEnvironment.LogWarning("无法获取程序路径，开机自启未写入。");
                    return;
                }

                key.SetValue(ValueName, "\"" + executable + "\"");
                AppEnvironment.Log("已写入开机自启项。");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppEnvironment.Log("已删除开机自启项。");
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("设置开机自启失败。", ex);
        }
    }
}
