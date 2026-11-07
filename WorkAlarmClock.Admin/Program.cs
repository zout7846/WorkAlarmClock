using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WorkAlarmClock.Admin;

/// <summary>
/// WorkAlarmClock 数据库管理工具入口：支持命令行子命令与交互式菜单。
/// 只读查询默认走只读连接；写操作（改设置 / 删除 / 清空）都会先自动备份并要求 y/N 确认。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些无控制台环境（重定向 / 服务）不允许改编码，保持默认即可。
        }
        catch (ArgumentException)
        {
            // 同上。
        }

        try
        {
            CommandLine commandLine = CommandLine.Parse(args);
            if (commandLine.ShowHelp || IsHelpCommand(commandLine.Command))
            {
                PrintHelp();
                return 0;
            }

            string dbPath = ResolveDbPath(commandLine.DbPath);
            AdminDatabase database = new AdminDatabase(dbPath);
            string command = commandLine.Command.ToLowerInvariant();
            if (command.Length == 0)
            {
                return RunInteractive(database);
            }

            if (!File.Exists(dbPath))
            {
                throw new AdminException("数据库不存在：" + Path.GetFullPath(dbPath) + "（可用 --db 或环境变量 WORKALARM_DB 指定）");
            }

            switch (command)
            {
                case "overview":
                    ShowOverview(database);
                    break;
                case "day":
                    ShowDay(database, RequirePositional(commandLine, 0, "day <yyyy-MM-dd>"));
                    break;
                case "summary":
                    ShowSummary(database, ParseDays(RequirePositional(commandLine, 0, "summary <N>")));
                    break;
                case "settings":
                    ShowSettings(database.LoadSettings(), database.HasSettings());
                    break;
                case "settings-set":
                    if (commandLine.Positionals.Count == 0)
                    {
                        throw new AdminException("用法：settings-set <键=值> [键=值 ...]，例如 settings-set WorkStartTime=09:00 LunchEnabled=false");
                    }

                    DoSettingsSet(database, commandLine.Positionals);
                    break;
                case "backup":
                    DoBackup(database);
                    break;
                case "delete-day":
                    DoDeleteDay(database, RequirePositional(commandLine, 0, "delete-day <yyyy-MM-dd>"));
                    break;
                case "clear-tasks":
                    DoClearTasks(database, RequirePositional(commandLine, 0, "clear-tasks <yyyy-MM-dd>"));
                    break;
                case "export":
                    string scope = commandLine.Positionals.Count > 0 ? commandLine.Positionals[0] : "all";
                    DoExport(database, scope, commandLine.OutputPath);
                    break;
                default:
                    throw new AdminException("未知命令：" + commandLine.Command + "（用 --help 查看用法）");
            }

            return 0;
        }
        catch (AdminException ex)
        {
            WriteError(ex.Message);
            return 1;
        }
        catch (SqliteException ex)
        {
            WriteError("数据库错误：" + ex.Message);
            return 1;
        }
        catch (IOException ex)
        {
            WriteError("文件读写错误：" + ex.Message);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteError("没有文件访问权限：" + ex.Message);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("WorkAlarmClock 数据库管理工具（控制台版）");
        Console.WriteLine("用法：WorkAlarmClock.Admin [--db <数据库路径>] <命令> [参数...]");
        Console.WriteLine();
        Console.WriteLine("命令：");
        Console.WriteLine("  overview                      概览（路径/大小/表行数/日期范围）");
        Console.WriteLine("  day <yyyy-MM-dd>              查看某天记录与流水");
        Console.WriteLine("  summary <N>                   最近 N 天工作/休息/午休汇总（1-366）");
        Console.WriteLine("  settings                      查看设置");
        Console.WriteLine("  settings-set <键=值> [...]    修改设置（写前自动备份，需 y/N 确认）");
        Console.WriteLine("  backup                        备份数据库（VACUUM INTO 到数据库目录\\backups）");
        Console.WriteLine("  delete-day <yyyy-MM-dd>       删除某天全部数据（需确认，写前自动备份）");
        Console.WriteLine("  clear-tasks <yyyy-MM-dd>      清空某天事项（需确认，写前自动备份）");
        Console.WriteLine("  export <yyyy-MM-dd|all>       导出流水 CSV（默认 all）");
        Console.WriteLine("  help                          显示本帮助");
        Console.WriteLine();
        Console.WriteLine("不指定命令时进入交互式菜单。");
        Console.WriteLine("数据库路径优先级：--db 参数 > 环境变量 WORKALARM_DB > %LOCALAPPDATA%\\WorkAlarmClock\\workalarm.db");
        Console.WriteLine();
        Console.WriteLine("可编辑设置键：WorkStartTime / WorkEndTime / LunchEnabled / LunchStartTime / LunchEndTime");
        Console.WriteLine("              / Workdays（1-7 表示周一至周日，逗号分隔）/ CountIdleAsWork");
        Console.WriteLine("示例：");
        Console.WriteLine("  WorkAlarmClock.Admin --db D:\\data\\workalarm.db overview");
        Console.WriteLine("  WorkAlarmClock.Admin summary 7");
        Console.WriteLine("  WorkAlarmClock.Admin settings-set WorkStartTime=09:00 LunchEnabled=true LunchStartTime=12:00 LunchEndTime=13:00");
        Console.WriteLine("环境变量示例：$env:WORKALARM_DB = 'D:\\data\\workalarm.db'");
    }

    private static bool IsHelpCommand(string command)
    {
        return command.Equals("help", StringComparison.OrdinalIgnoreCase)
            || command.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || command.Equals("--help", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDbPath(string? cliPath)
    {
        if (!string.IsNullOrWhiteSpace(cliPath))
        {
            return cliPath;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("WORKALARM_DB");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        if (string.IsNullOrEmpty(localAppData))
        {
            throw new AdminException("无法获取 LOCALAPPDATA 目录，请用 --db 或环境变量 WORKALARM_DB 指定数据库路径。");
        }

        return Path.Combine(localAppData, "WorkAlarmClock", "workalarm.db");
    }

    private static string RequirePositional(CommandLine commandLine, int index, string usage)
    {
        if (commandLine.Positionals.Count <= index)
        {
            throw new AdminException("缺少参数。用法：" + usage);
        }

        return commandLine.Positionals[index];
    }

    private static int ParseDays(string text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days < 1 || days > 366)
        {
            throw new AdminException("天数无效：" + text + "（应为 1-366 的整数）");
        }

        return days;
    }

    private static DateOnly ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            return date;
        }

        throw new AdminException("日期格式无效：" + text + "（应为 yyyy-MM-dd）");
    }

    private static string IsoDate(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static void WriteError(string message)
    {
        Console.Error.WriteLine("错误：" + message);
    }

    private static bool Confirm(string message)
    {
        Console.Write(message + " (y/N): ");
        string? line = Console.ReadLine();
        if (line is null)
        {
            Console.WriteLine();
            return false;
        }

        string text = line.Trim();
        return text.Equals("y", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("是", StringComparison.Ordinal)
            || text.Equals("1", StringComparison.Ordinal);
    }

    private static void ShowOverview(AdminDatabase database)
    {
        DatabaseOverview overview = database.GetOverview();
        Console.WriteLine("==== 数据库概览 ====");
        Console.WriteLine("数据库路径：" + overview.DbPath);
        if (!overview.Exists)
        {
            Console.WriteLine("文件状态：不存在");
            return;
        }

        Console.WriteLine("文件大小：" + AdminFormat.FileSize(overview.FileSizeBytes) + "（" + overview.FileSizeBytes + " 字节）");
        if (overview.WalSizeBytes > 0)
        {
            Console.WriteLine("WAL 大小：" + AdminFormat.FileSize(overview.WalSizeBytes) + "（" + overview.WalSizeBytes + " 字节）");
        }

        Console.WriteLine("schema_version：" + overview.SchemaVersion);
        Console.WriteLine("表行数：");
        if (overview.Tables.Count == 0)
        {
            Console.WriteLine("  （无业务表）");
        }

        foreach (TableRowCount table in overview.Tables)
        {
            Console.WriteLine("  " + table.Table.PadRight(20) + table.Rows.ToString(CultureInfo.InvariantCulture));
        }

        Console.WriteLine("日期范围：" + (overview.MinDate ?? "（无数据）") + " ~ " + (overview.MaxDate ?? "（无数据）"));
        Console.WriteLine("settings 键：" + (overview.SettingKeys.Count > 0 ? string.Join(", ", overview.SettingKeys) : "（无）"));
        if (overview.MetaRows.Count > 0)
        {
            List<string> meta = new List<string>();
            foreach (KeyValuePair<string, string> pair in overview.MetaRows)
            {
                meta.Add(pair.Key + " = " + pair.Value);
            }

            Console.WriteLine("meta：" + string.Join("；", meta));
        }
    }

    private static void ShowDay(AdminDatabase database, string dateText)
    {
        DateOnly date = ParseDate(dateText);
        string isoDate = IsoDate(date);
        DayDetail detail = database.GetDay(isoDate);
        AppSettingsDto settings = database.LoadSettings();

        Console.WriteLine("==== " + isoDate + " 记录 ====");
        Console.WriteLine(detail.HasDayRecord
            ? "day_records：存在（manual_ended=" + (detail.ManualEnded ? "是" : "否") + "）"
            : "day_records：不存在");
        Console.WriteLine("事项：" + detail.Tasks.Count + " 条，日报：" + detail.Reports.Count + " 条");

        Console.WriteLine("流水：" + detail.Transitions.Count + " 条");
        if (detail.Transitions.Count == 0)
        {
            Console.WriteLine("  （无）");
        }
        else
        {
            Console.WriteLine("  " + AdminFormat.PadVisual("序号", 6) + AdminFormat.PadVisual("本地时间", 21)
                + AdminFormat.PadVisual("原状态", 10) + AdminFormat.PadVisual("新状态", 12)
                + AdminFormat.PadVisual("来源", 8) + "备注");
            foreach (TransitionRow row in detail.Transitions)
            {
                Console.WriteLine("  "
                    + AdminFormat.PadVisual(row.Id.ToString(CultureInfo.InvariantCulture), 6)
                    + AdminFormat.PadVisual(AdminFormat.LocalDateTime(row.Time), 21)
                    + AdminFormat.PadVisual(AdminFormat.StateName(row.From), 10)
                    + AdminFormat.PadVisual(AdminFormat.StateText(row, settings), 12)
                    + AdminFormat.PadVisual(AdminFormat.TriggerSourceName(row.Source), 8)
                    + row.Note);
            }
        }

        if (detail.Tasks.Count > 0)
        {
            Console.WriteLine("事项列表：");
            foreach (TaskRow task in detail.Tasks)
            {
                string tag = string.IsNullOrEmpty(task.Tag) ? string.Empty : "[" + task.Tag + "] ";
                Console.WriteLine("  - " + AdminFormat.LocalDateTime(task.Timestamp) + "  " + tag + task.Text);
            }
        }

        if (detail.Reports.Count > 0)
        {
            Console.WriteLine("日报记录：");
            foreach (ReportRow report in detail.Reports)
            {
                Console.WriteLine("  - 生成于 " + AdminFormat.LocalDateTime(report.GeneratedAt) + " → " + report.FilePath);
                Console.WriteLine("    今日工作：" + report.TodayWork);
                Console.WriteLine("    明日计划：" + report.TomorrowPlan);
                Console.WriteLine("    风险与待办：" + report.Risks);
            }
        }
    }

    private static void ShowSummary(AdminDatabase database, int days)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        int count = Math.Clamp(days, 1, 366);
        DateOnly from = today.AddDays(-(count - 1));
        List<TransitionRow> transitions = database.GetTransitionsBetween(from, today);
        AppSettingsDto settings = database.LoadSettings();
        List<RecentDayRow> rows = SummaryCalculator.SummarizeRecentDays(transitions, today, count, settings);

        Console.WriteLine("==== 最近 " + count + " 天汇总（" + IsoDate(from) + " ~ " + IsoDate(today) + "）====");
        if (rows.Count == 0)
        {
            Console.WriteLine("该区间没有流水记录。");
            return;
        }

        Console.WriteLine("  " + AdminFormat.PadVisual("日期", 12) + AdminFormat.PadVisual("流水", 6)
            + AdminFormat.PadVisual("工作", 12) + AdminFormat.PadVisual("休息", 12) + "其中午休");
        TimeSpan totalWork = TimeSpan.Zero;
        TimeSpan totalRest = TimeSpan.Zero;
        TimeSpan totalLunch = TimeSpan.Zero;
        foreach (RecentDayRow row in rows)
        {
            totalWork += row.Work;
            totalRest += row.Rest;
            totalLunch += row.Lunch;
            Console.WriteLine("  "
                + AdminFormat.PadVisual(IsoDate(row.Date), 12)
                + AdminFormat.PadVisual(row.TransitionCount.ToString(CultureInfo.InvariantCulture), 6)
                + AdminFormat.PadVisual(AdminFormat.Duration(row.Work), 12)
                + AdminFormat.PadVisual(AdminFormat.Duration(row.Rest), 12)
                + AdminFormat.Duration(row.Lunch));
        }

        Console.WriteLine("  "
            + AdminFormat.PadVisual("合计", 12)
            + AdminFormat.PadVisual(string.Empty, 6)
            + AdminFormat.PadVisual(AdminFormat.Duration(totalWork), 12)
            + AdminFormat.PadVisual(AdminFormat.Duration(totalRest), 12)
            + AdminFormat.Duration(totalLunch));
        Console.WriteLine("说明：午休包含在休息内；空闲是否计入工时取决于设置 CountIdleAsWork="
            + (settings.CountIdleAsWork ? "是" : "否") + "。");
    }

    private static void ShowSettings(AppSettingsDto settings, bool stored)
    {
        Console.WriteLine("==== 当前设置 ====");
        if (!stored)
        {
            Console.WriteLine("（数据库中暂无 settings/app 记录，以下为主项目默认值；保存后会写入）");
        }

        Console.WriteLine("  " + "WorkStartTime".PadRight(18) + "上班时间       " + Hm(settings.WorkStartTime));
        Console.WriteLine("  " + "WorkEndTime".PadRight(18) + "下班时间       " + Hm(settings.WorkEndTime));
        Console.WriteLine("  " + "LunchEnabled".PadRight(18) + "启用午休       " + (settings.LunchEnabled ? "是" : "否"));
        Console.WriteLine("  " + "LunchStartTime".PadRight(18) + "午休开始       " + Hm(settings.LunchStartTime));
        Console.WriteLine("  " + "LunchEndTime".PadRight(18) + "午休结束       " + Hm(settings.LunchEndTime));
        Console.WriteLine("  " + "Workdays".PadRight(18) + "工作日         " + AdminFormat.Workdays(settings.Workdays));
        Console.WriteLine("  " + "CountIdleAsWork".PadRight(18) + "空闲计入工时   " + (settings.CountIdleAsWork ? "是" : "否"));
        int otherCount = settings.Other?.Count ?? 0;
        Console.WriteLine("  " + "其他设置项".PadRight(18) + otherCount + " 项（写回时原样保留）");
    }

    private static string Hm(TimeOnly time)
    {
        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static void DoSettingsSet(AdminDatabase database, IReadOnlyList<string> pairs)
    {
        AppSettingsDto before = database.LoadSettings();
        AppSettingsDto settings = database.LoadSettings();

        foreach (string pair in pairs)
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new AdminException("参数格式无效：" + pair + "（应为 键=值）");
            }

            string key = pair.Substring(0, separator);
            string value = pair.Substring(separator + 1);
            ApplySetting(settings, key, value);
        }

        List<string> changes = new List<string>();
        if (before.WorkStartTime != settings.WorkStartTime)
        {
            changes.Add("WorkStartTime: " + Hm(before.WorkStartTime) + " -> " + Hm(settings.WorkStartTime));
        }

        if (before.WorkEndTime != settings.WorkEndTime)
        {
            changes.Add("WorkEndTime: " + Hm(before.WorkEndTime) + " -> " + Hm(settings.WorkEndTime));
        }

        if (before.LunchEnabled != settings.LunchEnabled)
        {
            changes.Add("LunchEnabled: " + (before.LunchEnabled ? "是" : "否") + " -> " + (settings.LunchEnabled ? "是" : "否"));
        }

        if (before.LunchStartTime != settings.LunchStartTime)
        {
            changes.Add("LunchStartTime: " + Hm(before.LunchStartTime) + " -> " + Hm(settings.LunchStartTime));
        }

        if (before.LunchEndTime != settings.LunchEndTime)
        {
            changes.Add("LunchEndTime: " + Hm(before.LunchEndTime) + " -> " + Hm(settings.LunchEndTime));
        }

        if (!before.Workdays.SequenceEqual(settings.Workdays))
        {
            changes.Add("Workdays: " + AdminFormat.Workdays(before.Workdays) + " -> " + AdminFormat.Workdays(settings.Workdays));
        }

        if (before.CountIdleAsWork != settings.CountIdleAsWork)
        {
            changes.Add("CountIdleAsWork: " + (before.CountIdleAsWork ? "是" : "否") + " -> " + (settings.CountIdleAsWork ? "是" : "否"));
        }

        if (changes.Count == 0)
        {
            Console.WriteLine("没有发生任何修改。");
            return;
        }

        Console.WriteLine("即将修改：");
        foreach (string change in changes)
        {
            Console.WriteLine("  " + change);
        }

        ValidateOrThrow(settings);
        if (!Confirm("确认写回设置？"))
        {
            Console.WriteLine("已取消。");
            return;
        }

        SaveSettingsWithBackup(database, settings);
    }

    private static void SaveSettingsWithBackup(AdminDatabase database, AppSettingsDto settings)
    {
        Console.WriteLine("正在自动备份（VACUUM INTO）…");
        string backupPath = database.CreateBackup();
        Console.WriteLine("备份完成：" + backupPath);
        database.SaveSettings(settings);
        Console.WriteLine("设置已写回：" + database.FullPath);
    }

    private static void ApplySetting(AppSettingsDto settings, string key, string value)
    {
        switch (key.Trim().ToLowerInvariant())
        {
            case "workstarttime":
                settings.WorkStartTime = ParseTimeOrThrow(key, value);
                break;
            case "workendtime":
                settings.WorkEndTime = ParseTimeOrThrow(key, value);
                break;
            case "lunchenabled":
                settings.LunchEnabled = ParseBoolOrThrow(key, value);
                break;
            case "lunchstarttime":
                settings.LunchStartTime = ParseTimeOrThrow(key, value);
                break;
            case "lunchendtime":
                settings.LunchEndTime = ParseTimeOrThrow(key, value);
                break;
            case "workdays":
                settings.Workdays = ParseWorkdaysOrThrow(value);
                break;
            case "countidleaswork":
                settings.CountIdleAsWork = ParseBoolOrThrow(key, value);
                break;
            default:
                throw new AdminException(
                    "未知设置键：" + key + "（可用：WorkStartTime / WorkEndTime / LunchEnabled / LunchStartTime / LunchEndTime / Workdays / CountIdleAsWork）");
        }
    }

    private static TimeOnly ParseTimeOrThrow(string key, string value)
    {
        if (TryParseTime(value, out TimeOnly time))
        {
            return time;
        }

        throw new AdminException("时间格式无效：" + key + "=" + value + "（应为 HH:mm）");
    }

    private static bool ParseBoolOrThrow(string key, string value)
    {
        if (TryParseBool(value, out bool flag))
        {
            return flag;
        }

        throw new AdminException("布尔值无效：" + key + "=" + value + "（可用 true/false/y/n/1/0/是/否）");
    }

    private static bool[] ParseWorkdaysOrThrow(string value)
    {
        bool[] workdays = new bool[7];
        string[] parts = value.Split(
            new[] { ',', '，', '、', ' ', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < 1 || number > 7)
            {
                throw new AdminException("工作日格式无效：" + value + "（用 1-7 表示周一至周日，逗号分隔，如 1,2,3,4,5）");
            }

            int index = number == 7 ? 0 : number;
            workdays[index] = true;
        }

        return workdays;
    }

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "y":
            case "是":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "n":
            case "否":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParseTime(string text, out TimeOnly value)
    {
        string trimmed = text.Trim();
        string[] formats = { "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss" };
        if (TimeOnly.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }

        return TimeOnly.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static void ValidateOrThrow(AppSettingsDto settings)
    {
        List<string> errors = new List<string>();
        if (settings.WorkEndTime <= settings.WorkStartTime)
        {
            errors.Add("下班时间必须晚于上班时间。");
        }

        if (settings.LunchEnabled)
        {
            if (settings.LunchStartTime < settings.WorkStartTime)
            {
                errors.Add("午休开始时间不能早于上班时间。");
            }

            if (settings.LunchEndTime <= settings.LunchStartTime)
            {
                errors.Add("午休结束时间必须晚于午休开始时间。");
            }

            if (settings.LunchEndTime > settings.WorkEndTime)
            {
                errors.Add("午休结束时间不能晚于下班时间。");
            }
        }

        if (errors.Count > 0)
        {
            throw new AdminException("设置校验失败：" + string.Join("；", errors));
        }

        if (settings.Workdays.All(day => !day))
        {
            Console.WriteLine("警告：未选择任何工作日（主项目允许保存，但不会自动排班）。");
        }
    }

    private static void DoBackup(AdminDatabase database)
    {
        Console.WriteLine("正在备份（VACUUM INTO）…");
        string backupPath = database.CreateBackup();
        long size = new FileInfo(backupPath).Length;
        Console.WriteLine("备份完成：" + backupPath);
        Console.WriteLine("备份大小：" + AdminFormat.FileSize(size) + "（" + size + " 字节）");
    }

    private static void DoDeleteDay(AdminDatabase database, string dateText)
    {
        DateOnly date = ParseDate(dateText);
        string isoDate = IsoDate(date);
        DayDetail detail = database.GetDay(isoDate);
        int total = (detail.HasDayRecord ? 1 : 0) + detail.Transitions.Count + detail.Tasks.Count + detail.Reports.Count;
        if (total == 0)
        {
            Console.WriteLine(isoDate + " 没有可删除的记录。");
            return;
        }

        Console.WriteLine("即将删除 " + isoDate + " 的：day_record " + (detail.HasDayRecord ? "1" : "0")
            + " 条、流水 " + detail.Transitions.Count + " 条、事项 " + detail.Tasks.Count + " 条、日报 " + detail.Reports.Count + " 条。");
        Console.WriteLine("提示：建议先退出 WorkAlarmClock 应用再执行删除。");
        if (!Confirm("确认删除 " + isoDate + " 的全部数据？此操作不可撤销"))
        {
            Console.WriteLine("已取消。");
            return;
        }

        Console.WriteLine("正在自动备份（VACUUM INTO）…");
        string backupPath = database.CreateBackup();
        Console.WriteLine("备份完成：" + backupPath);

        DeleteResult result = database.DeleteDay(isoDate);
        Console.WriteLine("删除完成：day_records " + result.DayRecords + " 条、流水 " + result.Transitions
            + " 条、事项 " + result.Tasks + " 条、日报 " + result.Reports + " 条、提醒状态 " + result.ReminderStates + " 条。");
    }

    private static void DoClearTasks(AdminDatabase database, string dateText)
    {
        DateOnly date = ParseDate(dateText);
        string isoDate = IsoDate(date);
        DayDetail detail = database.GetDay(isoDate);
        if (detail.Tasks.Count == 0)
        {
            Console.WriteLine(isoDate + " 没有事项需要清空。");
            return;
        }

        Console.WriteLine("即将清空 " + isoDate + " 的 " + detail.Tasks.Count + " 条事项。");
        Console.WriteLine("提示：建议先退出 WorkAlarmClock 应用再执行清空。");
        if (!Confirm("确认清空 " + isoDate + " 的全部事项？此操作不可撤销"))
        {
            Console.WriteLine("已取消。");
            return;
        }

        Console.WriteLine("正在自动备份（VACUUM INTO）…");
        string backupPath = database.CreateBackup();
        Console.WriteLine("备份完成：" + backupPath);

        int deleted = database.ClearTasks(isoDate);
        Console.WriteLine("清空完成：删除事项 " + deleted + " 条。");
    }

    private static void DoExport(AdminDatabase database, string scopeText, string? outputPath)
    {
        string scope = (scopeText ?? string.Empty).Trim();
        string label;
        List<TransitionRow> rows;
        if (scope.Length == 0 || scope.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            label = "all";
            rows = database.GetTransitions(null);
        }
        else
        {
            DateOnly date = ParseDate(scope);
            label = IsoDate(date);
            rows = database.GetTransitions(label);
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("没有可导出的流水。");
            return;
        }

        AppSettingsDto settings = database.LoadSettings();
        string path = string.IsNullOrWhiteSpace(outputPath)
            ? CsvExporter.BuildDefaultPath(database.DbPath, label)
            : outputPath;
        int written = CsvExporter.Write(path, rows, settings);
        string fullPath = Path.GetFullPath(path);
        long size = new FileInfo(fullPath).Length;
        Console.WriteLine("已导出 " + written + " 条流水：" + fullPath);
        Console.WriteLine("文件大小：" + AdminFormat.FileSize(size) + "（" + size + " 字节，UTF-8 带 BOM）");
    }

    private static int RunInteractive(AdminDatabase database)
    {
        if (!File.Exists(database.DbPath))
        {
            WriteError("数据库不存在：" + database.FullPath + "（可用 --db 或环境变量 WORKALARM_DB 指定）");
            return 1;
        }

        Console.WriteLine("==== WorkAlarmClock 数据库管理工具 ====");
        Console.WriteLine("数据库：" + database.FullPath);
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("1) 概览");
            Console.WriteLine("2) 查看某天记录与流水");
            Console.WriteLine("3) 最近 N 天汇总");
            Console.WriteLine("4) 查看/修改设置");
            Console.WriteLine("5) 数据维护（备份 / 删除某天 / 清空任务）");
            Console.WriteLine("6) 导出 CSV");
            Console.WriteLine("0) 退出");
            Console.Write("请选择: ");
            string? choice = Console.ReadLine();
            if (choice is null)
            {
                Console.WriteLine();
                return 0;
            }

            try
            {
                switch (choice.Trim())
                {
                    case "0":
                        return 0;
                    case "1":
                        ShowOverview(database);
                        break;
                    case "2":
                        PromptAndShowDay(database);
                        break;
                    case "3":
                        PromptAndShowSummary(database);
                        break;
                    case "4":
                        RunSettingsMenu(database);
                        break;
                    case "5":
                        RunMaintenanceMenu(database);
                        break;
                    case "6":
                        RunExportMenu(database);
                        break;
                    default:
                        Console.WriteLine("无效选项，请输入 0-6。");
                        break;
                }
            }
            catch (AdminException ex)
            {
                WriteError(ex.Message);
            }
            catch (SqliteException ex)
            {
                WriteError("数据库错误：" + ex.Message);
            }
            catch (IOException ex)
            {
                WriteError("文件读写错误：" + ex.Message);
            }
        }
    }

    private static void PromptAndShowDay(AdminDatabase database)
    {
        Console.Write("日期 (yyyy-MM-dd，回车=今天): ");
        string? input = Console.ReadLine();
        if (input is null)
        {
            return;
        }

        string text = string.IsNullOrWhiteSpace(input)
            ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : input;
        ShowDay(database, text);
    }

    private static void PromptAndShowSummary(AdminDatabase database)
    {
        Console.Write("统计最近多少天（1-366，回车=7）: ");
        string? input = Console.ReadLine();
        if (input is null)
        {
            return;
        }

        string text = string.IsNullOrWhiteSpace(input) ? "7" : input;
        ShowSummary(database, ParseDays(text));
    }

    private static void RunSettingsMenu(AdminDatabase database)
    {
        AppSettingsDto settings = database.LoadSettings();
        bool stored = database.HasSettings();
        bool dirty = false;
        while (true)
        {
            ShowSettings(settings, stored || dirty);
            Console.WriteLine();
            Console.WriteLine("可修改：");
            Console.WriteLine("  1) 上班时间        2) 下班时间        3) 启用午休");
            Console.WriteLine("  4) 午休开始        5) 午休结束        6) 工作日");
            Console.WriteLine("  7) 空闲计入工时    0) 返回主菜单");
            Console.Write("请选择: ");
            string? choice = Console.ReadLine();
            if (choice is null)
            {
                return;
            }

            switch (choice.Trim())
            {
                case "0":
                    if (!dirty)
                    {
                        return;
                    }

                    if (!Confirm("是否保存以上修改？"))
                    {
                        Console.WriteLine("已放弃修改。");
                        return;
                    }

                    try
                    {
                        ValidateOrThrow(settings);
                    }
                    catch (AdminException ex)
                    {
                        WriteError(ex.Message);
                        Console.WriteLine("请继续修改，或按 0 放弃。");
                        break;
                    }

                    SaveSettingsWithBackup(database, settings);
                    stored = true;
                    dirty = false;
                    break;
                case "1":
                    if (TryPromptTime("新的上班时间 (HH:mm，回车取消): ", settings.WorkStartTime, out TimeOnly workStart))
                    {
                        settings.WorkStartTime = workStart;
                        dirty = true;
                    }

                    break;
                case "2":
                    if (TryPromptTime("新的下班时间 (HH:mm，回车取消): ", settings.WorkEndTime, out TimeOnly workEnd))
                    {
                        settings.WorkEndTime = workEnd;
                        dirty = true;
                    }

                    break;
                case "3":
                    if (TryPromptBool("是否启用午休 (y/N，回车取消): ", settings.LunchEnabled, out bool lunchEnabled))
                    {
                        settings.LunchEnabled = lunchEnabled;
                        dirty = true;
                    }

                    break;
                case "4":
                    if (TryPromptTime("新的午休开始时间 (HH:mm，回车取消): ", settings.LunchStartTime, out TimeOnly lunchStart))
                    {
                        settings.LunchStartTime = lunchStart;
                        dirty = true;
                    }

                    break;
                case "5":
                    if (TryPromptTime("新的午休结束时间 (HH:mm，回车取消): ", settings.LunchEndTime, out TimeOnly lunchEnd))
                    {
                        settings.LunchEndTime = lunchEnd;
                        dirty = true;
                    }

                    break;
                case "6":
                    Console.WriteLine("当前工作日：" + AdminFormat.Workdays(settings.Workdays));
                    Console.WriteLine("用 1-7 表示周一至周日，逗号分隔（如 1,2,3,4,5），直接回车取消。");
                    Console.Write("新的工作日: ");
                    string? workdaysText = Console.ReadLine();
                    if (workdaysText is null)
                    {
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(workdaysText))
                    {
                        Console.WriteLine("已取消。");
                        break;
                    }

                    settings.Workdays = ParseWorkdaysOrThrow(workdaysText);
                    dirty = true;
                    break;
                case "7":
                    if (TryPromptBool("空闲时段是否计入工作时长 (y/N，回车取消): ", settings.CountIdleAsWork, out bool countIdle))
                    {
                        settings.CountIdleAsWork = countIdle;
                        dirty = true;
                    }

                    break;
                default:
                    Console.WriteLine("无效选项，请输入 0-7。");
                    break;
            }
        }
    }

    private static bool TryPromptTime(string prompt, TimeOnly current, out TimeOnly value)
    {
        value = current;
        Console.Write(prompt);
        string? line = Console.ReadLine();
        if (line is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            Console.WriteLine("已取消。");
            return false;
        }

        if (!TryParseTime(line, out TimeOnly parsed))
        {
            Console.WriteLine("时间格式无效，应为 HH:mm。");
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryPromptBool(string prompt, bool current, out bool value)
    {
        value = current;
        Console.Write(prompt);
        string? line = Console.ReadLine();
        if (line is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            Console.WriteLine("已取消。");
            return false;
        }

        if (!TryParseBool(line, out bool parsed))
        {
            Console.WriteLine("输入无效，应为 y 或 n。");
            return false;
        }

        value = parsed;
        return true;
    }

    private static void RunMaintenanceMenu(AdminDatabase database)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("---- 数据维护 ----");
            Console.WriteLine("1) 备份数据库（VACUUM INTO）");
            Console.WriteLine("2) 删除某天记录（含流水，需确认并自动备份）");
            Console.WriteLine("3) 清空某天事项（需确认并自动备份）");
            Console.WriteLine("0) 返回");
            Console.Write("请选择: ");
            string? choice = Console.ReadLine();
            if (choice is null)
            {
                return;
            }

            switch (choice.Trim())
            {
                case "0":
                    return;
                case "1":
                    DoBackup(database);
                    break;
                case "2":
                    Console.Write("要删除的日期 (yyyy-MM-dd): ");
                    string? deleteDate = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(deleteDate))
                    {
                        Console.WriteLine("未输入日期，已取消。");
                        break;
                    }

                    try
                    {
                        DoDeleteDay(database, deleteDate);
                    }
                    catch (AdminException ex)
                    {
                        WriteError(ex.Message);
                    }

                    break;
                case "3":
                    Console.Write("要清空事项的日期 (yyyy-MM-dd): ");
                    string? clearDate = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(clearDate))
                    {
                        Console.WriteLine("未输入日期，已取消。");
                        break;
                    }

                    try
                    {
                        DoClearTasks(database, clearDate);
                    }
                    catch (AdminException ex)
                    {
                        WriteError(ex.Message);
                    }

                    break;
                default:
                    Console.WriteLine("无效选项。");
                    break;
            }
        }
    }

    private static void RunExportMenu(AdminDatabase database)
    {
        Console.Write("导出范围 (yyyy-MM-dd 或 all，回车=all): ");
        string? scope = Console.ReadLine();
        if (scope is null)
        {
            return;
        }

        string scopeText = string.IsNullOrWhiteSpace(scope) ? "all" : scope;
        Console.Write("输出文件路径（回车=数据库目录\\exports 自动命名）: ");
        string? output = Console.ReadLine();
        try
        {
            DoExport(database, scopeText, string.IsNullOrWhiteSpace(output) ? null : output);
        }
        catch (AdminException ex)
        {
            WriteError(ex.Message);
        }
    }

    /// <summary>命令行解析结果。</summary>
    private sealed class CommandLine
    {
        /// <summary>--db 指定的数据库路径。</summary>
        public string? DbPath { get; private set; }

        /// <summary>子命令（第一个非选项参数）。</summary>
        public string Command { get; private set; } = string.Empty;

        /// <summary>子命令位置参数。</summary>
        public List<string> Positionals { get; } = new List<string>();

        /// <summary>--out 指定的导出路径。</summary>
        public string? OutputPath { get; private set; }

        /// <summary>是否要求显示帮助。</summary>
        public bool ShowHelp { get; private set; }

        /// <summary>解析命令行。</summary>
        /// <param name="args">原始参数。</param>
        /// <returns>解析结果。</returns>
        public static CommandLine Parse(string[] args)
        {
            CommandLine result = new CommandLine();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--db")
                {
                    result.DbPath = RequireValue(args, ref i, "--db");
                }
                else if (arg.StartsWith("--db=", StringComparison.Ordinal))
                {
                    result.DbPath = arg.Substring("--db=".Length);
                }
                else if (arg == "--out")
                {
                    result.OutputPath = RequireValue(args, ref i, "--out");
                }
                else if (arg.StartsWith("--out=", StringComparison.Ordinal))
                {
                    result.OutputPath = arg.Substring("--out=".Length);
                }
                else if (arg == "--help" || arg == "-h")
                {
                    result.ShowHelp = true;
                }
                else if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1)
                {
                    throw new AdminException("未知选项：" + arg + "（用 --help 查看用法）");
                }
                else if (result.Command.Length == 0)
                {
                    result.Command = arg;
                }
                else
                {
                    result.Positionals.Add(arg);
                }
            }

            return result;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new AdminException("选项 " + option + " 缺少参数值。");
            }

            index++;
            return args[index];
        }
    }
}
