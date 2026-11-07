using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace WorkAlarmClock.Admin;

/// <summary>
/// SQLite 访问层：默认只用只读连接做查询，只有备份/写入时才打开读写连接。
/// </summary>
public sealed class AdminDatabase
{
    /// <summary>与主项目 StorageService 一致的 JSON 选项（TimeOnly / bool[] / 枚举 / 宽松转义）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] DataTables =
    {
        "day_records",
        "state_transitions",
        "work_tasks",
        "report_records",
        "reminder_states"
    };

    /// <summary>初始化数据库访问层。</summary>
    /// <param name="dbPath">数据库文件路径（可以是相对路径）。</param>
    public AdminDatabase(string dbPath)
    {
        DbPath = dbPath;
    }

    /// <summary>数据库文件路径（调用方传入的原始值）。</summary>
    public string DbPath { get; }

    /// <summary>数据库绝对路径。</summary>
    public string FullPath => Path.GetFullPath(DbPath);

    /// <summary>读取数据库概览（路径、大小、表行数、日期范围、settings/meta 键）。</summary>
    /// <returns>概览信息。</returns>
    public DatabaseOverview GetOverview()
    {
        FileInfo info = new FileInfo(DbPath);
        DatabaseOverview overview = new DatabaseOverview
        {
            DbPath = FullPath,
            Exists = info.Exists,
            FileSizeBytes = info.Exists ? info.Length : 0
        };

        string walPath = DbPath + "-wal";
        if (File.Exists(walPath))
        {
            overview.WalSizeBytes = new FileInfo(walPath).Length;
        }

        if (!info.Exists)
        {
            return overview;
        }

        using SqliteConnection connection = Open(readOnly: true);

        if (TableExists(connection, "schema_version"))
        {
            object? version = ExecuteScalar(connection, "SELECT MAX(version) FROM schema_version;");
            if (version is not null && version is not DBNull)
            {
                overview.SchemaVersion = Convert.ToString(version, CultureInfo.InvariantCulture) ?? "未知";
            }
        }

        foreach (string table in ListTables(connection))
        {
            overview.Tables.Add(new TableRowCount(table, CountRows(connection, table)));
        }

        string? minDate = null;
        string? maxDate = null;
        foreach (string table in new[] { "state_transitions", "day_records" })
        {
            if (!TableExists(connection, table))
            {
                continue;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(date), MAX(date) FROM \"" + table + "\";";
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
            {
                string candidateMin = reader.GetString(0);
                string candidateMax = reader.GetString(1);
                minDate = minDate is null || string.CompareOrdinal(candidateMin, minDate) < 0 ? candidateMin : minDate;
                maxDate = maxDate is null || string.CompareOrdinal(candidateMax, maxDate) > 0 ? candidateMax : maxDate;
            }
        }

        overview.MinDate = minDate;
        overview.MaxDate = maxDate;

        if (TableExists(connection, "settings"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT key FROM settings ORDER BY key;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                overview.SettingKeys.Add(reader.GetString(0));
            }
        }

        if (TableExists(connection, "meta"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM meta ORDER BY key;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                overview.MetaRows.Add(new KeyValuePair<string, string>(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
        }

        return overview;
    }

    /// <summary>读取指定日期的完整数据；date 为空时返回全部流水（用于导出）。</summary>
    /// <param name="date">yyyy-MM-dd；null 表示全部。</param>
    /// <returns>流水列表（按日期、时间升序）。</returns>
    public List<TransitionRow> GetTransitions(string? date)
    {
        using SqliteConnection connection = Open(readOnly: true);
        return ReadTransitions(connection, date, null, null);
    }

    /// <summary>读取日期区间内的流水（含两端）。</summary>
    /// <param name="from">起始日期。</param>
    /// <param name="to">结束日期。</param>
    /// <returns>流水列表。</returns>
    public List<TransitionRow> GetTransitionsBetween(DateOnly from, DateOnly to)
    {
        using SqliteConnection connection = Open(readOnly: true);
        string fromText = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toText = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return ReadTransitions(connection, null, fromText, toText);
    }

    /// <summary>读取某天的 day_record、流水、事项与日报。</summary>
    /// <param name="date">yyyy-MM-dd。</param>
    /// <returns>某天详情。</returns>
    public DayDetail GetDay(string date)
    {
        DayDetail detail = new DayDetail { Date = date };
        using SqliteConnection connection = Open(readOnly: true);

        if (TableExists(connection, "day_records"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT manual_ended FROM day_records WHERE date = @date;";
            command.Parameters.AddWithValue("@date", date);
            object? value = command.ExecuteScalar();
            if (value is not null && value is not DBNull)
            {
                detail.HasDayRecord = true;
                detail.ManualEnded = Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
            }
        }

        detail.Transitions.AddRange(ReadTransitions(connection, date, null, null));

        if (TableExists(connection, "work_tasks"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, timestamp, text, tag FROM work_tasks WHERE date = @date ORDER BY timestamp ASC;";
            command.Parameters.AddWithValue("@date", date);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                detail.Tasks.Add(new TaskRow
                {
                    Id = reader.GetString(0),
                    Timestamp = ParseIso(reader.GetString(1)),
                    Text = reader.GetString(2),
                    Tag = reader.GetString(3)
                });
            }
        }

        if (TableExists(connection, "report_records"))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT generated_at, file_path, today_work, tomorrow_plan, risks FROM report_records " +
                "WHERE date = @date ORDER BY generated_at ASC;";
            command.Parameters.AddWithValue("@date", date);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                detail.Reports.Add(new ReportRow
                {
                    GeneratedAt = ParseIso(reader.GetString(0)),
                    FilePath = reader.GetString(1),
                    TodayWork = reader.GetString(2),
                    TomorrowPlan = reader.GetString(3),
                    Risks = reader.GetString(4)
                });
            }
        }

        return detail;
    }

    /// <summary>读取 settings（key='app'）解析成的可编辑 DTO；缺失或无法解析时抛业务异常。</summary>
    /// <returns>设置 DTO（未知字段保留在 Other 中）。</returns>
    public AppSettingsDto LoadSettings()
    {
        using SqliteConnection connection = Open(readOnly: true);
        if (!TableExists(connection, "settings"))
        {
            return AppSettingsDto.CreateDefault();
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'app';";
        object? value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            return AppSettingsDto.CreateDefault();
        }

        string json = (string)value;
        AppSettingsDto? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettingsDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AdminException("settings JSON 解析失败：" + ex.Message);
        }

        if (settings is null)
        {
            throw new AdminException("settings JSON 为空。");
        }

        Normalize(settings);
        return settings;
    }

    /// <summary>判断数据库中是否存在 settings/app 记录。</summary>
    /// <returns>存在返回 true。</returns>
    public bool HasSettings()
    {
        using SqliteConnection connection = Open(readOnly: true);
        if (!TableExists(connection, "settings"))
        {
            return false;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM settings WHERE key = 'app';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>写回 settings（key='app'），保持未知字段与主项目一致的 JSON 格式。</summary>
    /// <param name="settings">设置 DTO。</param>
    public void SaveSettings(AppSettingsDto settings)
    {
        Normalize(settings);
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        using SqliteConnection connection = Open(readOnly: false);
        if (!TableExists(connection, "settings"))
        {
            throw new AdminException("数据库缺少 settings 表，可能不是 WorkAlarmClock 数据库。");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO settings (key, value) VALUES ('app', @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("@value", json);
        command.ExecuteNonQuery();
    }

    /// <summary>使用 VACUUM INTO 生成一致性备份，返回备份文件路径。</summary>
    /// <returns>备份文件绝对路径。</returns>
    public string CreateBackup()
    {
        string? directory = Path.GetDirectoryName(FullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new AdminException("无法确定数据库所在目录：" + FullPath);
        }

        string backupDirectory = Path.Combine(directory, "backups");
        Directory.CreateDirectory(backupDirectory);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string target = Path.Combine(backupDirectory, "workalarm-" + stamp + ".db");
        int suffix = 2;
        while (File.Exists(target))
        {
            target = Path.Combine(backupDirectory, "workalarm-" + stamp + "-" + suffix + ".db");
            suffix++;
        }

        using SqliteConnection connection = Open(readOnly: false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO @target;";
        command.Parameters.AddWithValue("@target", target);
        command.ExecuteNonQuery();
        return target;
    }

    /// <summary>删除某天的全部数据（day_record、流水、事项、日报、提醒状态），返回各表影响行数。</summary>
    /// <param name="date">yyyy-MM-dd。</param>
    /// <returns>各表删除行数。</returns>
    public DeleteResult DeleteDay(string date)
    {
        using SqliteConnection connection = Open(readOnly: false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            int dayRecords = DeleteByDate(connection, transaction, "day_records", date);
            int transitions = DeleteByDate(connection, transaction, "state_transitions", date);
            int tasks = DeleteByDate(connection, transaction, "work_tasks", date);
            int reports = DeleteByDate(connection, transaction, "report_records", date);
            int reminders = DeleteByDate(connection, transaction, "reminder_states", date);
            transaction.Commit();
            return new DeleteResult(dayRecords, transitions, tasks, reports, reminders);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>清空某天的全部事项，返回删除行数。</summary>
    /// <param name="date">yyyy-MM-dd。</param>
    /// <returns>删除行数。</returns>
    public int ClearTasks(string date)
    {
        using SqliteConnection connection = Open(readOnly: false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            int deleted = DeleteByDate(connection, transaction, "work_tasks", date);
            transaction.Commit();
            return deleted;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private SqliteConnection Open(bool readOnly)
    {
        SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
        };
        SqliteConnection connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static List<TransitionRow> ReadTransitions(
        SqliteConnection connection,
        string? date,
        string? fromDate,
        string? toDate)
    {
        List<TransitionRow> rows = new List<TransitionRow>();
        if (!TableExists(connection, "state_transitions"))
        {
            return rows;
        }

        using SqliteCommand command = connection.CreateCommand();
        if (date is not null)
        {
            command.CommandText =
                "SELECT id, date, time, from_state, to_state, source, note FROM state_transitions " +
                "WHERE date = @date ORDER BY time ASC;";
            command.Parameters.AddWithValue("@date", date);
        }
        else if (fromDate is not null && toDate is not null)
        {
            command.CommandText =
                "SELECT id, date, time, from_state, to_state, source, note FROM state_transitions " +
                "WHERE date >= @from AND date <= @to ORDER BY date ASC, time ASC;";
            command.Parameters.AddWithValue("@from", fromDate);
            command.Parameters.AddWithValue("@to", toDate);
        }
        else
        {
            command.CommandText =
                "SELECT id, date, time, from_state, to_state, source, note FROM state_transitions " +
                "ORDER BY date ASC, time ASC;";
        }

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TransitionRow
            {
                Id = reader.GetInt64(0),
                Date = reader.GetString(1),
                Time = ParseIso(reader.GetString(2)),
                From = (WorkState)reader.GetInt32(3),
                To = (WorkState)reader.GetInt32(4),
                Source = (TriggerSource)reader.GetInt32(5),
                Note = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
            });
        }

        return rows;
    }

    private static int DeleteByDate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string date)
    {
        if (!TableExists(connection, table))
        {
            return 0;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM \"" + table + "\" WHERE date = @date;";
        command.Parameters.AddWithValue("@date", date);
        return command.ExecuteNonQuery();
    }

    private static List<string> ListTables(SqliteConnection connection)
    {
        List<string> tables = new List<string>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static long CountRows(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"" + table + "\";";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static DateTimeOffset ParseIso(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value))
        {
            return value;
        }

        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd" };
        if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value))
        {
            return value;
        }

        return DateTimeOffset.MinValue;
    }

    private static void Normalize(AppSettingsDto settings)
    {
        if (settings.Workdays is null || settings.Workdays.Length != 7)
        {
            bool[] workdays = new bool[7];
            if (settings.Workdays is not null)
            {
                for (int i = 0; i < Math.Min(7, settings.Workdays.Length); i++)
                {
                    workdays[i] = settings.Workdays[i];
                }
            }

            settings.Workdays = workdays;
        }
    }
}
