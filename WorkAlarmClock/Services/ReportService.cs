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
/// 日报服务：固定 6 栏纯文本渲染（增量 R-27）、落盘（.txt 覆盖写，无 .bak）、复制。
/// 不再使用 Markdown 与自由占位符模板；仅保留可选单行页脚 ReportFooter。
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;
    private readonly ITimeTrackingService _timeTracking;
    private readonly ITicker _ticker;

    /// <summary>构造日报服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="storage">存储服务。</param>
    /// <param name="timeTracking">工时统计服务。</param>
    /// <param name="ticker">时钟节拍。</param>
    public ReportService(ISettingsService settings, IStorageService storage, ITimeTrackingService timeTracking, ITicker ticker)
    {
        _settings = settings;
        _storage = storage;
        _timeTracking = timeTracking;
        _ticker = ticker;
    }

    /// <summary>内置默认模板（与用户手写汇报格式一致）。</summary>
    public const string BuiltInTemplate =
        "{date}\n工作日报：\n今日工作：\n{todayWork}\n明日计划：\n{tomorrowPlan}\n问题风险：\n{risks}";

    /// <inheritdoc />
    public string DefaultReportTemplate => BuiltInTemplate;

    /// <inheritdoc />
    public string RenderText(string todayWork, string tomorrowPlan, string risks)
    {
        AppSettings settings = _settings.Current;
        DayRecord today = _timeTracking.Today;
        DateOnly date = DateOnly.FromDateTime(_ticker.Now.LocalDateTime);

        string template = string.IsNullOrWhiteSpace(settings.ReportTemplate)
            ? BuiltInTemplate
            : settings.ReportTemplate;

        string content = template
            .Replace("{date}", date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{todayWork}", BuildTodayWork(todayWork, today), StringComparison.Ordinal)
            .Replace("{tomorrowPlan}", OrNone(tomorrowPlan), StringComparison.Ordinal)
            .Replace("{risks}", OrNone(risks), StringComparison.Ordinal)
            .Replace("{work}", TextFormats.DurationToChineseSpaced(_timeTracking.WorkDuration), StringComparison.Ordinal)
            .Replace("{rest}", TextFormats.DurationToChineseSpaced(_timeTracking.RestDuration), StringComparison.Ordinal);

        // 统一换行符，去掉末尾多余空行，保留单个结尾换行。
        content = content.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        content = content.Replace("\n", Environment.NewLine);

        // 可选页脚（默认空）；非空则追加在最末。
        if (!string.IsNullOrWhiteSpace(settings.ReportFooter))
        {
            content += Environment.NewLine + Environment.NewLine + settings.ReportFooter.Trim();
        }

        return content + Environment.NewLine;
    }

    /// <summary>④今日工作：手填优先，留空则按事项流水生成「1. 内容」编号列表。</summary>
    /// <param name="todayWork">用户手填内容。</param>
    /// <param name="today">当日记录。</param>
    /// <returns>栏目正文。</returns>
    private static string BuildTodayWork(string todayWork, DayRecord today)
    {
        if (!string.IsNullOrWhiteSpace(todayWork))
        {
            return todayWork.Trim();
        }

        IReadOnlyList<WorkTaskItem> tasks = today.Tasks;
        if (tasks is null || tasks.Count == 0)
        {
            return "无";
        }

        List<WorkTaskItem> ordered = tasks.OrderBy(t => t.Timestamp).ToList();
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine();
            }

            builder.AppendFormat(CultureInfo.InvariantCulture, "{0}.{1}", i + 1, ordered[i].Text);
        }

        return builder.ToString();
    }

    /// <summary>空内容统一输出「无」，与手写模板保持一致。</summary>
    /// <param name="value">栏目内容。</param>
    /// <returns>内容或「无」。</returns>
    private static string OrNone(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "无" : value.Trim();

    /// <inheritdoc />
    public ReportRecord Generate(string todayWork, string tomorrowPlan, string risks)
    {
        string content = RenderText(todayWork, tomorrowPlan, risks);
        string directory = ResolveReportDirectory();
        Directory.CreateDirectory(directory);

        DateOnly date = DateOnly.FromDateTime(_ticker.Now.LocalDateTime);
        string path = GetReportPath(date);

        // 同日重复生成直接覆盖写（增量 R-27：不再生成 .bak）。
        // 输出格式由设置决定：仅 txt / 仅 md / 两者。
        string[] extensions = ResolveExtensions();
        foreach (string extension in extensions)
        {
            File.WriteAllText(Path.ChangeExtension(path, extension), content);
        }

        // 记录里保存主文件路径：优先 .txt，其次 .md。
        string primaryPath = Path.ChangeExtension(path, extensions.Contains(".txt") ? ".txt" : extensions[0]);

        ReportRecord record = new ReportRecord
        {
            GeneratedAt = _ticker.Now,
            FilePath = primaryPath,
            TodayWork = string.IsNullOrWhiteSpace(todayWork) ? "-" : todayWork.Trim(),
            TomorrowPlan = string.IsNullOrWhiteSpace(tomorrowPlan) ? "-" : tomorrowPlan.Trim(),
            Risks = string.IsNullOrWhiteSpace(risks) ? "-" : risks.Trim()
        };

        DayRecord today = _timeTracking.Today;
        today.Reports.Add(record);
        _storage.SaveDay(today);
        AppEnvironment.Log("日报已生成：" + path);
        return record;
    }

    /// <inheritdoc />
    public bool CopyToClipboard(string content)
    {
        try
        {
            System.Windows.Clipboard.SetText(content);
            return true;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("复制日报到剪贴板失败。", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public string ResolveReportDirectory()
    {
        string configured = _settings.Current.ReportDirectory;
        return string.IsNullOrWhiteSpace(configured) ? AppEnvironment.DefaultReportDirectory : configured;
    }

    /// <inheritdoc />
    public WeeklyReport GenerateWeekly(DateOnly anyDayInWeek)
    {
        AppSettings settings = _settings.Current;
        DateOnly monday = MondayOf(anyDayInWeek);
        DateOnly sunday = monday.AddDays(6);

        StringBuilder builder = new StringBuilder();
        builder.AppendLine(
            monday.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + " – " +
            sunday.ToString("MM.dd", CultureInfo.InvariantCulture) + " 周报");
        builder.AppendLine("本周工作：");

        TimeSpan total = TimeSpan.Zero;
        int workdays = 0;
        string lastPlan = "-";
        string lastRisks = "-";

        for (int i = 0; i < 7; i++)
        {
            DateOnly date = monday.AddDays(i);
            DayRecord? record = _storage.LoadDay(date);
            if (record is null)
            {
                continue;
            }

            ReplayResult replay = ReplayCalculator.Replay(record.Transitions, EndOfDay(date), settings);
            total += replay.Work;
            bool hasWork = replay.Work >= TimeSpan.FromMinutes(1);
            if (hasWork)
            {
                workdays++;
            }

            ReportRecord? report = record.Reports.LastOrDefault();
            if (report is not null)
            {
                if (!IsPlaceholder(report.TomorrowPlan))
                {
                    lastPlan = report.TomorrowPlan.Trim();
                }

                if (!IsPlaceholder(report.Risks))
                {
                    lastRisks = report.Risks.Trim();
                }
            }

            string body = BuildDayBlock(record, report);
            if (string.IsNullOrWhiteSpace(body) && !hasWork)
            {
                continue;
            }

            builder.AppendLine(string.Concat(
                "【",
                date.ToString("MM.dd", CultureInfo.InvariantCulture),
                " ",
                WeekName(date.DayOfWeek),
                "】工作 ",
                replay.Work.TotalHours.ToString("0.0", CultureInfo.InvariantCulture),
                "h"));
            builder.AppendLine(string.IsNullOrWhiteSpace(body) ? "无" : body);
        }

        double average = workdays == 0 ? 0 : total.TotalHours / workdays;
        builder.AppendLine(string.Concat(
            "本周统计：总工时 ",
            total.TotalHours.ToString("0.0", CultureInfo.InvariantCulture),
            "h / 出勤 ",
            workdays.ToString(CultureInfo.InvariantCulture),
            " 天 / 日均 ",
            average.ToString("0.0", CultureInfo.InvariantCulture),
            "h"));
        builder.AppendLine("下周计划：");
        builder.AppendLine(lastPlan == "-" ? "无" : lastPlan);
        builder.AppendLine("问题风险：");
        builder.AppendLine(lastRisks == "-" ? "无" : lastRisks);

        string content = builder.ToString()
            .Replace("\r\n", "\n")
            .TrimEnd('\n')
            .Replace("\n", Environment.NewLine) + Environment.NewLine;

        string directory = ResolveReportDirectory();
        Directory.CreateDirectory(directory);
        string stem = monday.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + "周报";
        string[] extensions = ResolveExtensions();
        foreach (string extension in extensions)
        {
            File.WriteAllText(Path.Combine(directory, stem + extension), content);
        }

        string primary = Path.Combine(directory, stem + (extensions.Contains(".txt") ? ".txt" : extensions[0]));
        AppEnvironment.Log("周报已生成：" + primary);
        return new WeeklyReport(content, primary);
    }

    /// <summary>取某天（含）所在周的周一。</summary>
    private static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>当日 23:59:59，用于闭合历史日期的开放工时段。</summary>
    private static DateTimeOffset EndOfDay(DateOnly date)
    {
        DateTime local = date.ToDateTime(new TimeOnly(23, 59, 59));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>取当日工作内容：优先日报里保存的「今日工作」，否则用事项流水编号。</summary>
    private static string BuildDayBlock(DayRecord record, ReportRecord? report)
    {
        if (report is not null && !IsPlaceholder(report.TodayWork))
        {
            return report.TodayWork.Trim();
        }

        if (record.Tasks is null || record.Tasks.Count == 0)
        {
            return string.Empty;
        }

        List<WorkTaskItem> ordered = record.Tasks.OrderBy(t => t.Timestamp).ToList();
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine();
            }

            string tagPrefix = string.IsNullOrWhiteSpace(ordered[i].Tag)
                ? string.Empty
                : "[" + ordered[i].Tag.Trim() + "] ";
            builder.AppendFormat(CultureInfo.InvariantCulture, "{0}.{1}{2}", i + 1, tagPrefix, ordered[i].Text);
        }

        return builder.ToString();
    }

    /// <summary>占位符判定（"-" 或空白视为没有内容）。</summary>
    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == "-";

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

    /// <inheritdoc />
    public string GetReportPath(DateOnly date) =>
        Path.Combine(ResolveReportDirectory(), FileStem(date) + ResolveExtensions()[0]);

    /// <summary>按设置解析输出扩展名集合（0=仅 txt，1=仅 md，2=两者）。</summary>
    private string[] ResolveExtensions() => _settings.Current.ReportFileFormat switch
    {
        0 => new[] { ".txt" },
        1 => new[] { ".md" },
        _ => new[] { ".txt", ".md" }
    };

    /// <summary>日报文件名（不含扩展名）：2026.11.04工作日报。</summary>
    private static string FileStem(DateOnly date) =>
        date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + "工作日报";
}
