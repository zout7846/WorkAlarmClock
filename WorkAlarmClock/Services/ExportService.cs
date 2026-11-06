using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// CSV 导出服务：工时、事项、日报三张表，UTF-8 BOM 以便 Excel 正确识别中文。
/// 纯手写 CSV（逗号分隔 + 双引号转义），不引入任何第三方库。
/// </summary>
public sealed class ExportService : IExportService
{
    /// <summary>单次导出的最大天数。</summary>
    private const int MaxDays = 1000;

    private readonly IStorageService _storage;
    private readonly ISettingsService _settings;

    /// <summary>构造导出服务。</summary>
    /// <param name="storage">存储服务。</param>
    /// <param name="settings">设置服务（重放需按当前设置判定）。</param>
    public ExportService(IStorageService storage, ISettingsService settings)
    {
        _storage = storage;
        _settings = settings;
    }

    /// <inheritdoc />
    public string ExportRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WorkAlarmClock",
        "Export");

    /// <inheritdoc />
    public IReadOnlyList<string> ExportAll(string? directory = null)
    {
        List<string> files = new List<string>();
        try
        {
            string target = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(ExportRoot, DateTime.Now.ToString("yyyy.MM.dd_HHmmss", CultureInfo.InvariantCulture))
                : directory;
            Directory.CreateDirectory(target);

            IReadOnlyList<DateOnly> dates = _storage.ListDayDates(MaxDays);

            files.Add(WriteDays(target, dates));
            files.Add(WriteTasks(target, dates));
            files.Add(WriteReports(target, dates));

            AppEnvironment.Log("数据已导出：" + target);
            return files;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("导出数据失败。", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>工时表：日期 / 星期 / 工作分钟 / 休息分钟 / 午休分钟。</summary>
    private string WriteDays(string directory, IReadOnlyList<DateOnly> dates)
    {
        string path = Path.Combine(directory, "work_days.csv");
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("日期,星期,工作分钟,休息分钟,午休分钟");

        foreach (DateOnly date in dates.OrderBy(d => d))
        {
            DayRecord? record = _storage.LoadDay(date);
            TimeSpan work = TimeSpan.Zero;
            TimeSpan rest = TimeSpan.Zero;
            TimeSpan lunch = TimeSpan.Zero;

            if (record is not null)
            {
                DateTime local = date.ToDateTime(new TimeOnly(23, 59, 59));
                ReplayResult replay = ReplayCalculator.Replay(
                    record.Transitions,
                    new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)),
                    _settings.Current);
                work = replay.Work;
                rest = replay.Rest;
                lunch = replay.Lunch;
            }

            builder.AppendLine(string.Join(",", new[]
            {
                Csv(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(WeekName(date.DayOfWeek)),
                ((int)Math.Round(work.TotalMinutes)).ToString(CultureInfo.InvariantCulture),
                ((int)Math.Round(rest.TotalMinutes)).ToString(CultureInfo.InvariantCulture),
                ((int)Math.Round(lunch.TotalMinutes)).ToString(CultureInfo.InvariantCulture)
            }));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        return path;
    }

    /// <summary>事项表：日期 / 时间 / 标签 / 内容。</summary>
    private string WriteTasks(string directory, IReadOnlyList<DateOnly> dates)
    {
        string path = Path.Combine(directory, "tasks.csv");
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("日期,时间,标签,内容");

        foreach (DateOnly date in dates.OrderBy(d => d))
        {
            DayRecord? record = _storage.LoadDay(date);
            if (record is null)
            {
                continue;
            }

            foreach (WorkTaskItem task in record.Tasks.OrderBy(t => t.Timestamp))
            {
                builder.AppendLine(string.Join(",", new[]
                {
                    Csv(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Csv(task.Timestamp.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)),
                    Csv(string.IsNullOrWhiteSpace(task.Tag) ? "未分类" : task.Tag),
                    Csv(task.Text)
                }));
            }
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        return path;
    }

    /// <summary>日报清单表：日期 / 生成时间 / 文件路径。</summary>
    private string WriteReports(string directory, IReadOnlyList<DateOnly> dates)
    {
        string path = Path.Combine(directory, "reports.csv");
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("日期,生成时间,文件路径");

        foreach (DateOnly date in dates.OrderBy(d => d))
        {
            DayRecord? record = _storage.LoadDay(date);
            if (record is null)
            {
                continue;
            }

            foreach (ReportRecord report in record.Reports.OrderBy(r => r.GeneratedAt))
            {
                builder.AppendLine(string.Join(",", new[]
                {
                    Csv(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    Csv(report.GeneratedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    Csv(report.FilePath)
                }));
            }
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        return path;
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时用双引号包裹并把引号翻倍。</summary>
    private static string Csv(string? value)
    {
        string text = value ?? string.Empty;
        bool needQuote = text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        return needQuote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static string WeekName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
}
