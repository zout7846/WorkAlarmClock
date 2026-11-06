using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 基于 SQLite 的本地持久化服务（增量 R-25）。
/// 单一长连接 + WAL + 事务，强杀重启无重复流水；ReplayCalculator 重算逻辑不变，仅数据源切换。
/// 设置存于 settings 表的 JSON 列；每日数据落 day_records / state_transitions / work_tasks / report_records；
/// 提醒状态落 reminder_states（修复重启重复提醒）。旧 JSON 首次启动自动迁移到 data_legacy_backup/。
/// </summary>
public sealed class StorageService : IStorageService, IDisposable
{
    /// <summary>全应用唯一 JSON 选项：缩进、中文不转义、枚举存字符串、允许 NaN 字面量（桌宠位置记忆默认值）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SqliteConnection _connection;

    /// <summary>构造并打开数据库连接，执行 DDL，必要时迁移旧 JSON。</summary>
    public StorageService()
    {
        _connection = OpenConnection();
        EnsureSchema();
        MigrateLegacyJson();
    }

    /// <inheritdoc />
    public AppSettings LoadSettings()
    {
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = @key;";
            command.Parameters.AddWithValue("@key", "app");
            object? value = command.ExecuteScalar();
            if (value is null or DBNull)
            {
                return AppSettings.CreateDefault();
            }

            string json = value.ToString() ?? string.Empty;
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                throw new InvalidOperationException("设置反序列化结果为空。");
            }

            NormalizeSettings(settings);
            return settings;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("读取设置失败，使用默认设置。", ex);
            return AppSettings.CreateDefault();
        }
    }

    /// <inheritdoc />
    public void SaveSettings(AppSettings settings)
    {
        try
        {
            NormalizeSettings(settings);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO settings(key, value) VALUES(@key, @value) " +
                "ON CONFLICT(key) DO UPDATE SET value = @value;";
            command.Parameters.AddWithValue("@key", "app");
            command.Parameters.AddWithValue("@value", json);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("保存设置失败。", ex);
        }
    }

    /// <inheritdoc />
    public DayRecord? LoadDay(DateOnly date)
    {
        string dateText = TextFormats.DateToIso(date);
        try
        {
            bool exists;
            bool manualEnded;
            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT manual_ended FROM day_records WHERE date = @date;";
                command.Parameters.AddWithValue("@date", dateText);
                object? value = command.ExecuteScalar();
                if (value is null or DBNull)
                {
                    return null;
                }

                exists = true;
                manualEnded = Convert.ToInt32(value) != 0;
            }

            DayRecord record = new DayRecord { Date = date, ManualEnded = manualEnded };

            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT time, from_state, to_state, source, note FROM state_transitions " +
                    "WHERE date = @date ORDER BY time ASC;";
                command.Parameters.AddWithValue("@date", dateText);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    record.Transitions.Add(new StateTransition
                    {
                        Time = ParseIso((string)reader.GetString(0)),
                        From = (WorkState)reader.GetInt32(1),
                        To = (WorkState)reader.GetInt32(2),
                        Source = (TriggerSource)reader.GetInt32(3),
                        Note = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT id, timestamp, text, tag FROM work_tasks WHERE date = @date;";
                command.Parameters.AddWithValue("@date", dateText);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    record.Tasks.Add(new WorkTaskItem
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        Timestamp = ParseIso((string)reader.GetString(1)),
                        Text = reader.GetString(2),
                        Tag = reader.IsDBNull(3) ? string.Empty : reader.GetString(3)
                    });
                }
            }

            using (SqliteCommand command = _connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT generated_at, file_path, today_work, tomorrow_plan, risks FROM report_records WHERE date = @date;";
                command.Parameters.AddWithValue("@date", dateText);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    record.Reports.Add(new ReportRecord
                    {
                        GeneratedAt = ParseIso((string)reader.GetString(0)),
                        FilePath = reader.GetString(1),
                        TodayWork = reader.IsDBNull(2) ? "-" : reader.GetString(2),
                        TomorrowPlan = reader.IsDBNull(3) ? "-" : reader.GetString(3),
                        Risks = reader.IsDBNull(4) ? "-" : reader.GetString(4)
                    });
                }
            }

            _ = exists;
            return record;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("读取当日数据失败：" + dateText, ex);
            return null;
        }
    }

    /// <inheritdoc />
    public void SaveDay(DayRecord record)
    {
        string dateText = TextFormats.DateToIso(record.Date);
        try
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            try
            {
                // 事务幂等覆盖：先清空当日三张子表，再全量重插，天然避免异常重启的重复行。
                ExecuteNonQuery(transaction,
                    "DELETE FROM state_transitions WHERE date = @date;", ("@date", dateText));
                ExecuteNonQuery(transaction,
                    "DELETE FROM work_tasks WHERE date = @date;", ("@date", dateText));
                ExecuteNonQuery(transaction,
                    "DELETE FROM report_records WHERE date = @date;", ("@date", dateText));

                ExecuteNonQuery(transaction,
                    "INSERT INTO day_records(date, manual_ended) VALUES(@date, @m) " +
                    "ON CONFLICT(date) DO UPDATE SET manual_ended = @m;",
                    ("@date", dateText), ("@m", record.ManualEnded ? 1 : 0));

                foreach (StateTransition transition in record.Transitions)
                {
                    ExecuteNonQuery(transaction,
                        "INSERT INTO state_transitions(date, time, from_state, to_state, source, note) " +
                        "VALUES(@date, @time, @from, @to, @source, @note);",
                        ("@date", dateText),
                        ("@time", transition.Time.ToString("O", CultureInfo.InvariantCulture)),
                        ("@from", (int)transition.From),
                        ("@to", (int)transition.To),
                        ("@source", (int)transition.Source),
                        ("@note", transition.Note ?? string.Empty));
                }

                foreach (WorkTaskItem task in record.Tasks)
                {
                    ExecuteNonQuery(transaction,
                        "INSERT INTO work_tasks(id, date, timestamp, text, tag) VALUES(@id, @date, @ts, @text, @tag);",
                        ("@id", task.Id.ToString()),
                        ("@date", dateText),
                        ("@ts", task.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                        ("@text", task.Text),
                        ("@tag", task.Tag ?? string.Empty));
                }

                foreach (ReportRecord report in record.Reports)
                {
                    ExecuteNonQuery(transaction,
                        "INSERT INTO report_records(date, generated_at, file_path, today_work, tomorrow_plan, risks) " +
                        "VALUES(@date, @ts, @path, @work, @plan, @risks);",
                        ("@date", dateText),
                        ("@ts", report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)),
                        ("@path", report.FilePath),
                        ("@work", report.TodayWork ?? "-"),
                        ("@plan", report.TomorrowPlan ?? "-"),
                        ("@risks", report.Risks ?? "-"));
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("保存当日数据失败：" + dateText, ex);
        }
    }

    /// <inheritdoc />
    public ReminderState? LoadReminderState(DateOnly date)
    {
        string dateText = TextFormats.DateToIso(date);
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "SELECT weak_fired, weak_ignored, weak_deferred_until, strong_fired, strong_confirmed, strong_repeat_count " +
                "FROM reminder_states WHERE date = @date;";
            command.Parameters.AddWithValue("@date", dateText);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            ReminderState state = new ReminderState
            {
                WeakFired = reader.GetInt32(0) != 0,
                WeakIgnored = reader.GetInt32(1) != 0,
                StrongFired = reader.GetInt32(3) != 0,
                StrongConfirmed = reader.GetInt32(4) != 0,
                StrongRepeatCount = reader.GetInt32(5)
            };

            if (!reader.IsDBNull(2))
            {
                state.WeakDeferredUntil = ParseIso(reader.GetString(2));
            }

            return state;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("读取提醒状态失败：" + dateText, ex);
            return null;
        }
    }

    /// <inheritdoc />
    public void SaveReminderState(DateOnly date, ReminderState state)
    {
        string dateText = TextFormats.DateToIso(date);
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO reminder_states(date, weak_fired, weak_ignored, weak_deferred_until, " +
                "strong_fired, strong_confirmed, strong_repeat_count) " +
                "VALUES(@date, @wf, @wi, @wd, @sf, @sc, @src) " +
                "ON CONFLICT(date) DO UPDATE SET " +
                "weak_fired=@wf, weak_ignored=@wi, weak_deferred_until=@wd, " +
                "strong_fired=@sf, strong_confirmed=@sc, strong_repeat_count=@src;";
            command.Parameters.AddWithValue("@date", dateText);
            command.Parameters.AddWithValue("@wf", state.WeakFired ? 1 : 0);
            command.Parameters.AddWithValue("@wi", state.WeakIgnored ? 1 : 0);
            command.Parameters.AddWithValue("@wd", state.WeakDeferredUntil?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sf", state.StrongFired ? 1 : 0);
            command.Parameters.AddWithValue("@sc", state.StrongConfirmed ? 1 : 0);
            command.Parameters.AddWithValue("@src", state.StrongRepeatCount);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("保存提醒状态失败：" + dateText, ex);
        }
    }

    /// <inheritdoc />
    public void BackupTo(string filePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // VACUUM INTO 生成一致性快照（含 WAL 中未落盘的内容），比直接拷贝 .db 安全。
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "VACUUM INTO @target;";
            command.Parameters.AddWithValue("@target", filePath);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("数据库备份失败：" + filePath, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DateOnly> ListReportDates(int max)
    {
        List<DateOnly> dates = new List<DateOnly>();
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT date FROM report_records ORDER BY date DESC LIMIT @max;";
            command.Parameters.AddWithValue("@max", Math.Clamp(max, 1, 1000));

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (DateOnly.TryParseExact(
                    reader.GetString(0),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly date))
                {
                    dates.Add(date);
                }
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("读取日报日期列表失败。", ex);
        }

        return dates;
    }

    /// <inheritdoc />
    public IReadOnlyList<DateOnly> ListDayDates(int max)
    {
        List<DateOnly> dates = new List<DateOnly>();
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT date FROM day_records ORDER BY date DESC LIMIT @max;";
            command.Parameters.AddWithValue("@max", Math.Clamp(max, 1, 1000));

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (DateOnly.TryParseExact(
                    reader.GetString(0),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly date))
                {
                    dates.Add(date);
                }
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("读取记录日期列表失败。", ex);
        }

        return dates;
    }

    /// <inheritdoc />
    public IReadOnlyList<DayRecord> LoadWeek(DateOnly today)
    {
        List<DayRecord> week = new List<DayRecord>();
        int offsetToMonday = ((int)today.DayOfWeek + 6) % 7;
        DateOnly monday = today.AddDays(-offsetToMonday);
        for (int i = 0; i <= offsetToMonday; i++)
        {
            DateOnly date = monday.AddDays(i);
            DayRecord? record = LoadDay(date);
            if (record is not null)
            {
                week.Add(record);
            }
        }

        return week;
    }

    /// <inheritdoc />
    public void EnsureArchive(DateOnly today)
    {
        // SQLite 数据常驻库内，按 date 分区即等价归档，无需搬运文件。
        AppEnvironment.Log("跨天归档检查完成，当前日期：" + TextFormats.DateToIso(today));
    }

    /// <summary>打开数据库连接；若库损坏则备份 .corrupt 并重建空库。</summary>
    /// <returns>已打开的连接。</returns>
    private SqliteConnection OpenConnection()
    {
        string path = AppEnvironment.DbPath;
        try
        {
            SqliteConnection connection = new SqliteConnection("Data Source=" + path);
            connection.Open();
            return connection;
        }
        catch (SqliteException ex) when (IsMalformed(ex))
        {
            try
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
                AppEnvironment.Log("SQLite 数据库损坏，已备份为 .corrupt 并重建空库。", ex);
            }
            catch (Exception backupEx)
            {
                AppEnvironment.Log("备份损坏数据库失败。", backupEx);
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception deleteEx)
            {
                AppEnvironment.Log("删除损坏数据库失败。", deleteEx);
            }

            SqliteConnection rebuilt = new SqliteConnection("Data Source=" + path);
            rebuilt.Open();
            return rebuilt;
        }
    }

    /// <summary>判断 SqliteException 是否为"数据库映像损坏"。</summary>
    private static bool IsMalformed(SqliteException ex) =>
        ex.SqliteErrorCode is 11 or 517 or 523 or 267 or 265; // SQLITE_CORRUPT 系列

    /// <summary>执行幂等 DDL（建表 / 索引 / 迁移标记）。</summary>
    private void EnsureSchema()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode=WAL; " +
            "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL); " +
            "INSERT OR IGNORE INTO schema_version(version) VALUES(1); " +
            "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL); " +
            "CREATE TABLE IF NOT EXISTS day_records(date TEXT PRIMARY KEY, manual_ended INTEGER NOT NULL DEFAULT 0); " +
            "CREATE TABLE IF NOT EXISTS state_transitions(" +
            "  id INTEGER PRIMARY KEY AUTOINCREMENT, date TEXT NOT NULL, time TEXT NOT NULL, " +
            "  from_state INTEGER NOT NULL, to_state INTEGER NOT NULL, source INTEGER NOT NULL, note TEXT); " +
            "CREATE INDEX IF NOT EXISTS ix_transitions_date_time ON state_transitions(date, time); " +
            "CREATE TABLE IF NOT EXISTS work_tasks(id TEXT PRIMARY KEY, date TEXT NOT NULL, timestamp TEXT NOT NULL, text TEXT NOT NULL, tag TEXT NOT NULL DEFAULT ''); " +
            "CREATE INDEX IF NOT EXISTS ix_tasks_date ON work_tasks(date); " +
            "CREATE TABLE IF NOT EXISTS report_records(" +
            "  id INTEGER PRIMARY KEY AUTOINCREMENT, date TEXT NOT NULL, generated_at TEXT NOT NULL, " +
            "  file_path TEXT NOT NULL, today_work TEXT NOT NULL DEFAULT '-', " +
            "  tomorrow_plan TEXT NOT NULL, risks TEXT NOT NULL); " +
            "CREATE INDEX IF NOT EXISTS ix_reports_date ON report_records(date); " +
            "CREATE TABLE IF NOT EXISTS reminder_states(" +
            "  date TEXT PRIMARY KEY, weak_fired INTEGER NOT NULL DEFAULT 0, weak_ignored INTEGER NOT NULL DEFAULT 0, " +
            "  weak_deferred_until TEXT, strong_fired INTEGER NOT NULL DEFAULT 0, " +
            "  strong_confirmed INTEGER NOT NULL DEFAULT 0, strong_repeat_count INTEGER NOT NULL DEFAULT 0); " +
            "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);";
        command.ExecuteNonQuery();

        // 增量：老库补列（v1.2 起日报支持可编辑"今日工作"）。
        EnsureReportTodayWorkColumn();

        // 增量：老库补列（v1.8 起事项支持标签）。
        EnsureTaskTagColumn();
    }

    /// <summary>为已存在的 work_tasks 表补 tag 列（幂等）。</summary>
    private void EnsureTaskTagColumn()
    {
        try
        {
            bool hasColumn = false;
            using (SqliteCommand info = _connection.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(work_tasks);";
                using SqliteDataReader reader = info.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "tag", StringComparison.OrdinalIgnoreCase))
                    {
                        hasColumn = true;
                        break;
                    }
                }
            }

            if (hasColumn)
            {
                return;
            }

            using SqliteCommand alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE work_tasks ADD COLUMN tag TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();
            AppEnvironment.Log("数据库迁移：work_tasks 已新增 tag 列。");
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("数据库迁移 tag 列失败。", ex);
        }
    }

    /// <summary>为已存在的 report_records 表补 today_work 列（幂等）。</summary>
    private void EnsureReportTodayWorkColumn()
    {
        try
        {
            bool hasColumn = false;
            using (SqliteCommand info = _connection.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(report_records);";
                using SqliteDataReader reader = info.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "today_work", StringComparison.OrdinalIgnoreCase))
                    {
                        hasColumn = true;
                        break;
                    }
                }
            }

            if (hasColumn)
            {
                return;
            }

            using SqliteCommand alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE report_records ADD COLUMN today_work TEXT NOT NULL DEFAULT '-';";
            alter.ExecuteNonQuery();
            AppEnvironment.Log("数据库迁移：report_records 已新增 today_work 列。");
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("数据库迁移 today_work 列失败。", ex);
        }
    }

    /// <summary>一次性迁移旧 JSON（data/*.json + settings.json）到 SQLite 并归档到 data_legacy_backup/。</summary>
    private void MigrateLegacyJson()
    {
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'migration_done';";
            object? done = command.ExecuteScalar();
            if (done is not null and not DBNull)
            {
                return;
            }

            AppEnvironment.EnsureDirectories();
            string dataDir = AppEnvironment.DataDirectory;
            if (!Directory.Exists(dataDir))
            {
                MarkMigrationDone();
                return;
            }

            bool anyMigrated = false;
            foreach (string file in Directory.GetFiles(dataDir, "*.json"))
            {
                string fileName = Path.GetFileName(file);
                if (!DateOnly.TryParseExact(fileName, "yyyy-MM-dd.json", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                {
                    continue;
                }

                try
                {
                    string json = File.ReadAllText(file);
                    DayRecord? record = JsonSerializer.Deserialize<DayRecord>(json, JsonOptions);
                    if (record is not null)
                    {
                        record.Date = date;
                        record.Transitions ??= new List<StateTransition>();
                        record.Tasks ??= new List<WorkTaskItem>();
                        record.Reports ??= new List<ReportRecord>();
                        SaveDay(record);
                        anyMigrated = true;
                    }
                }
                catch (Exception ex)
                {
                    AppEnvironment.Log("迁移当日 JSON 失败：" + fileName, ex);
                }
            }

            string settingsPath = AppEnvironment.SettingsPath;
            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings is not null)
                    {
                        NormalizeSettings(settings);
                        SaveSettings(settings);
                        anyMigrated = true;
                    }
                }
                catch (Exception ex)
                {
                    AppEnvironment.Log("迁移 settings.json 失败。", ex);
                }
            }

            // 迁移成功后把旧 json 移入归档目录（保留不删除）。
            if (anyMigrated || File.Exists(settingsPath) || Directory.GetFiles(dataDir, "*.json").Length > 0)
            {
                try
                {
                    string backupDir = AppEnvironment.DataLegacyBackupDirectory;
                    Directory.CreateDirectory(backupDir);
                    foreach (string file in Directory.GetFiles(dataDir, "*.json"))
                    {
                        string dest = Path.Combine(backupDir, Path.GetFileName(file));
                        File.Move(file, dest, overwrite: true);
                    }

                    if (File.Exists(settingsPath))
                    {
                        File.Move(settingsPath, Path.Combine(backupDir, "settings.json"), overwrite: true);
                    }
                }
                catch (Exception ex)
                {
                    AppEnvironment.Log("归档旧 JSON 失败。", ex);
                }
            }

            MarkMigrationDone();
            AppEnvironment.Log("旧 JSON 数据迁移完成（已归档到 data_legacy_backup/）。");
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("旧 JSON 迁移过程出现异常，保留旧文件并新建库运行。", ex);
        }
    }

    private void MarkMigrationDone()
    {
        try
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO meta(key, value) VALUES('migration_done', '1') " +
                "ON CONFLICT(key) DO UPDATE SET value='1';";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("写入迁移标记失败。", ex);
        }
    }

    /// <summary>确保工作日数组长度合法，避免手工编辑 json 导致越界。</summary>
    /// <param name="settings">设置实例。</param>
    private static void NormalizeSettings(AppSettings settings)
    {
        if (settings.Workdays is null || settings.Workdays.Length != 7)
        {
            bool[] workdays = new bool[7];
            if (settings.Workdays is not null)
            {
                Array.Copy(settings.Workdays, workdays, Math.Min(settings.Workdays.Length, 7));
            }

            settings.Workdays = workdays;
        }

        settings.ReportDirectory ??= string.Empty;
        settings.ReportFooter ??= string.Empty;
        settings.Hotkeys ??= new HotkeySettings();
    }

    /// <summary>执行参数化非查询（单条）。</summary>
    private static void ExecuteNonQuery(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>把 ISO 字符串解析回 DateTimeOffset（失败回退最小时间）。</summary>
    private static DateTimeOffset ParseIso(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset result)
            ? result
            : DateTimeOffset.MinValue;

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _connection.Dispose();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("关闭数据库连接失败。", ex);
        }
    }
}
