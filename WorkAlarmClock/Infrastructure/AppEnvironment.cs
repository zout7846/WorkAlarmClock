using System;
using System.Globalization;
using System.IO;

namespace WorkAlarmClock.Infrastructure;

/// <summary>
/// 应用运行环境约定：目录、日志与文本格式化。
/// </summary>
public static class AppEnvironment
{
    /// <summary>应用名（同时用于目录名与互斥体名）。</summary>
    public const string AppName = "WorkAlarmClock";

    /// <summary>单实例互斥体名称（Local\ 前缀保证仅当前用户会话）。</summary>
    public static string SingleInstanceMutexName => @"Local\WorkAlarmClock_SingleInstance" + TestSuffix;

    /// <summary>测试隔离后缀（环境变量 WORKALARM_TEST_SUFFIX），正式运行恒为空。</summary>
    private static string TestSuffix =>
        Environment.GetEnvironmentVariable("WORKALARM_TEST_SUFFIX") is { Length: > 0 } suffix ? "_" + suffix : string.Empty;

    /// <summary>根数据目录：%LOCALAPPDATA%\WorkAlarmClock（测试隔离时追加后缀或使用 WORKALARM_TEST_ROOT）。</summary>
    public static string RootDirectory =>
        Environment.GetEnvironmentVariable("WORKALARM_TEST_ROOT") is { Length: > 0 } testRoot
            ? testRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName + TestSuffix);

    /// <summary>每日数据目录：%LOCALAPPDATA%\WorkAlarmClock\data。</summary>
    public static string DataDirectory => Path.Combine(RootDirectory, "data");

    /// <summary>日志目录：%LOCALAPPDATA%\WorkAlarmClock\logs。</summary>
    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>配置文件路径：%LOCALAPPDATA%\WorkAlarmClock\settings.json。</summary>
    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>默认日报目录：我的文档\WorkAlarmClock\Reports。</summary>
    public static string DefaultReportDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppName, "Reports");

    /// <summary>默认截图目录：我的图片\WorkAlarmClock\Screenshots。</summary>
    public static string DefaultScreenshotDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName, "Screenshots");

    /// <summary>SQLite 数据库文件路径：%LOCALAPPDATA%\WorkAlarmClock\workalarm.db。</summary>
    public static string DbPath => Path.Combine(RootDirectory, "workalarm.db");

    /// <summary>旧 JSON 数据一次性迁移后的归档目录：%LOCALAPPDATA%\WorkAlarmClock\data_legacy_backup。</summary>
    public static string DataLegacyBackupDirectory => Path.Combine(RootDirectory, "data_legacy_backup");

    /// <summary>创建数据目录、日志目录与旧数据备份目录（失败静默）。</summary>
    public static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(DataLegacyBackupDirectory);
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    /// <summary>获取指定日期的数据文件路径。</summary>
    /// <param name="date">日期。</param>
    /// <returns>完整文件路径。</returns>
    public static string GetDayFilePath(DateOnly date) =>
        Path.Combine(DataDirectory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    /// <summary>记录普通信息日志。</summary>
    /// <param name="message">日志内容。</param>
    public static void Log(string message) => Write("INFO", message, null);

    /// <summary>记录警告日志。</summary>
    /// <param name="message">日志内容。</param>
    public static void LogWarning(string message) => Write("WARN", message, null);

    /// <summary>记录异常日志。</summary>
    /// <param name="ex">异常对象。</param>
    public static void Log(Exception ex) => Write("ERROR", ex.Message, ex);

    /// <summary>记录异常日志并附加上下文说明。</summary>
    /// <param name="message">上下文说明。</param>
    /// <param name="ex">异常对象。</param>
    public static void Log(string message, Exception ex) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            string file = Path.Combine(LogDirectory, "app-" + DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".log");
            string line = string.Concat(
                "[", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "] ",
                "[", level, "] ", message);
            if (ex is not null)
            {
                line = string.Concat(line, Environment.NewLine, ex);
            }

            File.AppendAllText(file, line + Environment.NewLine);
        }
        catch
        {
            // Logging must not interrupt the application.
        }
    }
}

/// <summary>
/// 时间与时长格式化工具（界面文案统一走这里）。
/// </summary>
public static class TextFormats
{
    /// <summary>时长格式化为 HH:mm（超过 24 小时按总小时显示）。</summary>
    /// <param name="duration">时长。</param>
    /// <returns>例如 "07:42"。</returns>
    public static string DurationToHm(TimeSpan duration)
    {
        TimeSpan safe = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        int totalMinutes = (int)Math.Floor(safe.TotalMinutes);
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}", hours, minutes);
    }

    /// <summary>时长格式化为 HH:mm:ss。</summary>
    /// <param name="duration">时长。</param>
    /// <returns>例如 "07:42:09"。</returns>
    public static string DurationToHms(TimeSpan duration)
    {
        TimeSpan safe = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        int totalSeconds = (int)Math.Floor(safe.TotalSeconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}",
            totalSeconds / 3600, (totalSeconds / 60) % 60, totalSeconds % 60);
    }

    /// <summary>时长格式化为紧凑中文（日报占位符使用）。</summary>
    /// <param name="duration">时长。</param>
    /// <returns>例如 "7小时42分"。</returns>
    public static string DurationToChinese(TimeSpan duration)
    {
        TimeSpan safe = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        int totalMinutes = (int)Math.Floor(safe.TotalMinutes);
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return hours > 0 ? string.Format(CultureInfo.InvariantCulture, "{0}小时{1}分", hours, minutes)
                         : string.Format(CultureInfo.InvariantCulture, "{0}分钟", minutes);
    }

    /// <summary>时长格式化为带空格的中文（气泡文案使用）。</summary>
    /// <param name="duration">时长。</param>
    /// <returns>例如 "3 小时 25 分钟"。</returns>
    public static string DurationToChineseSpaced(TimeSpan duration)
    {
        TimeSpan safe = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        int totalMinutes = (int)Math.Floor(safe.TotalMinutes);
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return hours > 0 ? string.Format(CultureInfo.InvariantCulture, "{0} 小时 {1} 分钟", hours, minutes)
                         : string.Format(CultureInfo.InvariantCulture, "{0} 分钟", minutes);
    }

    /// <summary>时刻格式化为 HH:mm。</summary>
    /// <param name="time">时刻。</param>
    /// <returns>例如 "09:41"。</returns>
    public static string TimeToHm(DateTimeOffset time) =>
        time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>时刻格式化为 HH:mm:ss。</summary>
    /// <param name="time">时刻。</param>
    /// <returns>例如 "09:41:23"。</returns>
    public static string TimeToHms(DateTimeOffset time) =>
        time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>作息时刻格式化为 HH:mm。</summary>
    /// <param name="time">日内时刻。</param>
    /// <returns>例如 "18:00"。</returns>
    public static string TimeOnlyToHm(TimeOnly time) =>
        time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>日期格式化为 yyyy-MM-dd。</summary>
    /// <param name="date">日期。</param>
    /// <returns>例如 "2026-03-05"。</returns>
    public static string DateToIso(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>字符串截断（托盘 tooltip 63 字符限制）。</summary>
    /// <param name="text">原文。</param>
    /// <param name="maxLength">最大长度。</param>
    /// <returns>截断后的文本。</returns>
    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength);
    }
}
