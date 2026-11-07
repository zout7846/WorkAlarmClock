using System.Globalization;

namespace WorkAlarmClock.Admin;

/// <summary>
/// 控制台输出格式化（状态/来源中文名、时长、日期时间、文件大小）。
/// </summary>
public static class AdminFormat
{
    private static readonly string[] WeekdayNames =
    {
        "周日",
        "周一",
        "周二",
        "周三",
        "周四",
        "周五",
        "周六"
    };

    /// <summary>状态中文名（不含休息子类型）。</summary>
    /// <param name="state">状态。</param>
    /// <returns>中文名。</returns>
    public static string StateName(WorkState state)
    {
        return state switch
        {
            WorkState.BeforeWork => "待上班",
            WorkState.Working => "工作中",
            WorkState.Resting => "休息中",
            WorkState.OffWork => "已下班",
            _ => "未知(" + (int)state + ")"
        };
    }

    /// <summary>休息来源中文名。</summary>
    /// <param name="source">休息来源。</param>
    /// <returns>中文名。</returns>
    public static string RestSourceName(RestSource source)
    {
        return source switch
        {
            RestSource.Lunch => "午休",
            RestSource.LockScreen => "锁屏",
            RestSource.Idle => "空闲",
            RestSource.Manual => "手动",
            _ => "未知"
        };
    }

    /// <summary>触发来源中文名。</summary>
    /// <param name="source">触发来源。</param>
    /// <returns>中文名。</returns>
    public static string TriggerSourceName(TriggerSource source)
    {
        return source switch
        {
            TriggerSource.Schedule => "作息表",
            TriggerSource.Manual => "手动",
            TriggerSource.Lock => "锁屏",
            TriggerSource.Idle => "空闲",
            TriggerSource.System => "系统",
            _ => "未知(" + (int)source + ")"
        };
    }

    /// <summary>把流水行的“新状态”渲染成中文（休息按来源细分）。</summary>
    /// <param name="row">流水行。</param>
    /// <param name="settings">设置（用于判断 System 来源是否落在午休窗口）。</param>
    /// <returns>状态文本。</returns>
    public static string StateText(TransitionRow row, AppSettingsDto settings)
    {
        if (row.To != WorkState.Resting)
        {
            return StateName(row.To);
        }

        RestSource source = ResolveRestSource(row, settings);
        return source switch
        {
            RestSource.Lunch => "午休中",
            RestSource.LockScreen => "锁屏休息",
            RestSource.Idle => "空闲休息",
            RestSource.Manual => "休息中",
            _ => "休息中"
        };
    }

    /// <summary>根据触发来源推断休息来源（与主项目 ReplayCalculator 一致）。</summary>
    /// <param name="row">流水行。</param>
    /// <param name="settings">设置。</param>
    /// <returns>休息来源；非休息状态返回 <see cref="RestSource.Manual"/>。</returns>
    public static RestSource ResolveRestSource(TransitionRow row, AppSettingsDto settings)
    {
        return row.Source switch
        {
            TriggerSource.Schedule => RestSource.Lunch,
            TriggerSource.Lock => RestSource.LockScreen,
            TriggerSource.Idle => RestSource.Idle,
            TriggerSource.Manual => RestSource.Manual,
            _ => IsWithinLunch(row.Time, settings) ? RestSource.Lunch : RestSource.Idle
        };
    }

    /// <summary>判断本地时间是否落在午休窗口内（主项目 IsWithinLunch 语义）。</summary>
    /// <param name="time">时刻。</param>
    /// <param name="settings">设置。</param>
    /// <returns>是否在午休窗口内。</returns>
    public static bool IsWithinLunch(DateTimeOffset time, AppSettingsDto settings)
    {
        if (!settings.LunchEnabled)
        {
            return false;
        }

        TimeOnly localTime = TimeOnly.FromDateTime(time.LocalDateTime);
        return localTime >= settings.LunchStartTime && localTime < settings.LunchEndTime;
    }

    /// <summary>时长格式化为 “7小时42分”（与主项目 TextFormats.DurationToChinese 一致）。</summary>
    /// <param name="duration">时长。</param>
    /// <returns>中文时长。</returns>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        long totalMinutes = (long)Math.Floor(duration.TotalMinutes);
        long hours = totalMinutes / 60;
        long minutes = totalMinutes % 60;
        if (hours <= 0)
        {
            return minutes + "分";
        }

        return hours + "小时" + minutes + "分";
    }

    /// <summary>本地日期时间 “yyyy-MM-dd HH:mm:ss”。</summary>
    /// <param name="time">时刻。</param>
    /// <returns>格式文本。</returns>
    public static string LocalDateTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>本地时间 “HH:mm:ss”。</summary>
    /// <param name="time">时刻。</param>
    /// <returns>格式文本。</returns>
    public static string LocalTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>工作日的周几列表（如 “周一,周二”）。</summary>
    /// <param name="workdays">7 元素布尔数组（下标 = DayOfWeek）。</param>
    /// <returns>中文列表。</returns>
    public static string Workdays(bool[]? workdays)
    {
        if (workdays is null || workdays.Length != 7)
        {
            return "（未设置）";
        }

        List<string> names = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if (workdays[i])
            {
                names.Add(WeekdayNames[i]);
            }
        }

        return names.Count == 0 ? "（无）" : string.Join(",", names);
    }

    /// <summary>按显示宽度右补空格（中文等全角字符按 2 列计），保证控制台表格对齐。</summary>
    /// <param name="text">文本。</param>
    /// <param name="width">目标显示宽度。</param>
    /// <returns>补空格后的文本。</returns>
    public static string PadVisual(string? text, int width)
    {
        string value = text ?? string.Empty;
        int current = VisualWidth(value);
        return current >= width ? value : value + new string(' ', width - current);
    }

    /// <summary>文件大小格式化（字节 / KB / MB）。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>可读大小。</returns>
    public static string FileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        return (kb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    private static int VisualWidth(string text)
    {
        int width = 0;
        foreach (char ch in text)
        {
            width += IsWideChar(ch) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWideChar(char ch)
    {
        return ch >= 0x1100
            && (ch <= 0x115F
                || ch == 0x2329
                || ch == 0x232A
                || (ch >= 0x2E80 && ch <= 0xA4CF)
                || (ch >= 0xAC00 && ch <= 0xD7A3)
                || (ch >= 0xF900 && ch <= 0xFAFF)
                || (ch >= 0xFE30 && ch <= 0xFE6F)
                || (ch >= 0xFF00 && ch <= 0xFF60)
                || (ch >= 0xFFE0 && ch <= 0xFFE6));
    }
}
