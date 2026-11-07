using System.Globalization;
using System.Text;

namespace WorkAlarmClock.Admin;

/// <summary>
/// 流水 CSV 导出（UTF-8 带 BOM，Excel 可直接识别中文）。
/// </summary>
public static class CsvExporter
{
    /// <summary>生成默认导出路径：&lt;数据库目录&gt;\exports\transitions-&lt;范围&gt;-&lt;时间戳&gt;.csv。</summary>
    /// <param name="dbPath">数据库路径。</param>
    /// <param name="scopeLabel">范围标签（yyyy-MM-dd 或 all）。</param>
    /// <returns>CSV 绝对路径。</returns>
    public static string BuildDefaultPath(string dbPath, string scopeLabel)
    {
        string fullPath = Path.GetFullPath(dbPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new AdminException("无法确定数据库所在目录：" + fullPath);
        }

        string exportDirectory = Path.Combine(directory, "exports");
        Directory.CreateDirectory(exportDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(exportDirectory, "transitions-" + scopeLabel + "-" + stamp + ".csv");
    }

    /// <summary>把流水写入 CSV 文件。</summary>
    /// <param name="filePath">目标文件路径（目录不存在会自动创建）。</param>
    /// <param name="rows">流水行。</param>
    /// <param name="settings">设置（用于把休息状态细分为午休/锁屏等）。</param>
    /// <returns>实际写入的行数（不含表头）。</returns>
    public static int Write(string filePath, IReadOnlyList<TransitionRow> rows, AppSettingsDto settings)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.Append("id,日期,时间,原状态码,原状态,新状态码,新状态,来源码,来源,备注");
        builder.Append("\r\n");

        foreach (TransitionRow row in rows)
        {
            builder.Append(row.Id.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(row.Date)).Append(',');
            builder.Append(Escape(AdminFormat.LocalDateTime(row.Time))).Append(',');
            builder.Append(((int)row.From).ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(AdminFormat.StateName(row.From))).Append(',');
            builder.Append(((int)row.To).ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(AdminFormat.StateText(row, settings))).Append(',');
            builder.Append(((int)row.Source).ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(Escape(AdminFormat.TriggerSourceName(row.Source))).Append(',');
            builder.Append(Escape(row.Note));
            builder.Append("\r\n");
        }

        File.WriteAllText(filePath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return rows.Count;
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool needsQuote = value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal);
        if (!needsQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
