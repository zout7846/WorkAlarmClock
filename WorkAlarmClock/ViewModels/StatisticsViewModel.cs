using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.ViewModels;

/// <summary>柱状图里的一天。</summary>
public sealed class DayBarItem
{
    /// <summary>日期文案（11.04）。</summary>
    public string DateText { get; init; } = string.Empty;

    /// <summary>星期文案（周一）。</summary>
    public string WeekText { get; init; } = string.Empty;

    /// <summary>工作时长柱高（像素）。</summary>
    public double WorkHeight { get; init; }

    /// <summary>休息时长柱高（像素）。</summary>
    public double RestHeight { get; init; }

    /// <summary>工时文案（7.5h）。</summary>
    public string WorkText { get; init; } = string.Empty;

    /// <summary>悬浮提示。</summary>
    public string Tooltip { get; init; } = string.Empty;

    /// <summary>柱宽。</summary>
    public double BarWidth { get; init; }

    /// <summary>是否显示日期标签。</summary>
    public bool ShowLabel { get; init; }

    /// <summary>是否工作日（决定标签颜色）。</summary>
    public bool IsWorkday { get; init; }
}

/// <summary>时间轴色块。</summary>
public sealed class TimelineBarItem
{
    /// <summary>左偏移（画布坐标）。</summary>
    public double Left { get; init; }

    /// <summary>宽度（画布坐标）。</summary>
    public double Width { get; init; }

    /// <summary>填充色。</summary>
    public Brush Fill { get; init; } = Brushes.Transparent;

    /// <summary>悬浮提示。</summary>
    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>
/// 统计窗口视图模型：近 N 天柱状图 + 今日时间轴 + 汇总。
/// 全部数据由 StatisticsService 重放流水得出，图表用 WPF 原生元素绘制（零第三方）。
/// </summary>
public sealed class StatisticsViewModel : ObservableObject
{
    /// <summary>柱状图绘图区高度（像素）。</summary>
    private const double ChartHeight = 132;

    /// <summary>时间轴画布宽度（逻辑像素，外层 Viewbox 负责缩放）。</summary>
    private const double TimelineWidth = 560;

    private int _rangeDays = 7;
    private string _totalWorkText = "-";
    private string _averageWorkText = "-";
    private string _bestDayText = "-";
    private string _rangeTitle = "近 7 天";
    private bool _hasData;

    /// <summary>构造统计视图模型。</summary>
    public StatisticsViewModel()
    {
        Bars = new ObservableCollection<DayBarItem>();
        Timeline = new ObservableCollection<TimelineBarItem>();
        Legend = new ObservableCollection<TimelineLegendItem>
        {
            new TimelineLegendItem("工作", "#FF22C55E"),
            new TimelineLegendItem("午休", "#FFF08A3C"),
            new TimelineLegendItem("休息", "#FFF5B740"),
            new TimelineLegendItem("已下班", "#FF9AA7B8")
        };

        RangeCommand = new RelayCommand(parameter => SetRange(parameter?.ToString()));
        RefreshCommand = new RelayCommand(Refresh);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        Refresh();
    }

    /// <summary>每日柱状数据。</summary>
    public ObservableCollection<DayBarItem> Bars { get; }

    /// <summary>今日时间轴色块。</summary>
    public ObservableCollection<TimelineBarItem> Timeline { get; }

    /// <summary>时间轴图例。</summary>
    public ObservableCollection<TimelineLegendItem> Legend { get; }

    /// <summary>近 N 天标题。</summary>
    public string RangeTitle
    {
        get => _rangeTitle;
        private set => SetProperty(ref _rangeTitle, value);
    }

    /// <summary>区间总工时。</summary>
    public string TotalWorkText
    {
        get => _totalWorkText;
        private set => SetProperty(ref _totalWorkText, value);
    }

    /// <summary>日均工时。</summary>
    public string AverageWorkText
    {
        get => _averageWorkText;
        private set => SetProperty(ref _averageWorkText, value);
    }

    /// <summary>最长的一天。</summary>
    public string BestDayText
    {
        get => _bestDayText;
        private set => SetProperty(ref _bestDayText, value);
    }

    /// <summary>是否有可展示的数据。</summary>
    public bool HasData
    {
        get => _hasData;
        private set => SetProperty(ref _hasData, value);
    }

    /// <summary>切换范围命令（参数为天数）。</summary>
    public RelayCommand RangeCommand { get; }

    /// <summary>刷新命令。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>关闭窗口命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    private void SetRange(string? days)
    {
        if (int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
        {
            _rangeDays = value;
            Refresh();
        }
    }

    /// <summary>重新拉取统计数据并重建图表。</summary>
    public void Refresh()
    {
        try
        {
            IReadOnlyList<DayStat> days = AppServices.Statistics.GetRecentDays(_rangeDays);
            RangeTitle = "近 " + _rangeDays.ToString(CultureInfo.InvariantCulture) + " 天";

            double maxTotal = days.Count == 0
                ? 1
                : Math.Max(1, days.Max(d => Math.Max(0, d.Work.TotalHours + d.Rest.TotalHours)));
            double barWidth = _rangeDays switch
            {
                <= 7 => 34,
                <= 14 => 20,
                _ => 11
            };

            Bars.Clear();
            double sum = 0;
            DayStat? best = null;

            for (int i = 0; i < days.Count; i++)
            {
                DayStat day = days[i];
                double work = Math.Max(0, day.Work.TotalHours);
                double rest = Math.Max(0, day.Rest.TotalHours);
                sum += work;

                if (best is null || work > best.Work.TotalHours)
                {
                    best = day;
                }

                bool showLabel = _rangeDays <= 14 || i % 5 == 0 || i == days.Count - 1;
                Bars.Add(new DayBarItem
                {
                    DateText = day.Date.ToString("MM.dd", CultureInfo.InvariantCulture),
                    WeekText = WeekName(day.Date.DayOfWeek),
                    WorkHeight = work <= 0 ? 0 : Math.Max(2, work / maxTotal * ChartHeight),
                    RestHeight = rest <= 0 ? 0 : Math.Max(2, rest / maxTotal * ChartHeight),
                    WorkText = FormatHours(day.Work),
                    Tooltip = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1}：工作 {2}，休息 {3}",
                        day.Date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
                        WeekName(day.Date.DayOfWeek),
                        FormatHours(day.Work),
                        FormatHours(day.Rest)),
                    BarWidth = barWidth,
                    ShowLabel = showLabel,
                    IsWorkday = day.IsWorkday
                });
            }

            TimeSpan total = TimeSpan.FromHours(sum);
            int workdayCount = days.Count(d => d.IsWorkday);
            TotalWorkText = FormatHours(total);
            AverageWorkText = workdayCount == 0
                ? "-"
                : FormatHours(TimeSpan.FromHours(sum / workdayCount));
            BestDayText = best is null || best.Work <= TimeSpan.Zero
                ? "-"
                : best.Date.ToString("MM.dd", CultureInfo.InvariantCulture) + " · " + FormatHours(best.Work);

            HasData = sum > 0;

            BuildTimeline();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("加载统计数据失败。", ex);
        }
    }

    /// <summary>把今日时间轴分段换算成画布坐标。</summary>
    private void BuildTimeline()
    {
        Timeline.Clear();
        IReadOnlyList<TimelineSlice> slices = AppServices.Statistics.GetTodayTimeline();
        if (slices.Count == 0)
        {
            return;
        }

        DateTimeOffset start = slices.Min(s => s.Start);
        DateTimeOffset end = slices.Max(s => s.End);
        if (end - start < TimeSpan.FromMinutes(30))
        {
            end = start.AddMinutes(30);
        }

        double total = (end - start).TotalMinutes;
        foreach (TimelineSlice slice in slices)
        {
            double left = Math.Max(0, (slice.Start - start).TotalMinutes / total * TimelineWidth);
            double width = Math.Max(1, (slice.End - slice.Start).TotalMinutes / total * TimelineWidth);
            Timeline.Add(new TimelineBarItem
            {
                Left = left,
                Width = Math.Min(width, TimelineWidth - left),
                Fill = ColorFor(slice.Kind),
                Tooltip = slice.Label
            });
        }
    }

    private static Brush ColorFor(string kind)
    {
        string hex = kind switch
        {
            "work" => "#FF22C55E",
            "lunch" => "#FFF08A3C",
            "off" => "#FF9AA7B8",
            _ => "#FFF5B740"
        };

        SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
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

    private static string FormatHours(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return "0h";
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}h", span.TotalHours);
    }
}

/// <summary>时间轴图例项。</summary>
public sealed class TimelineLegendItem
{
    /// <summary>构造图例项。</summary>
    /// <param name="name">名称。</param>
    /// <param name="hex">颜色。</param>
    public TimelineLegendItem(string name, string hex)
    {
        Name = name;
        SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Fill = brush;
    }

    /// <summary>名称。</summary>
    public string Name { get; }

    /// <summary>色块画刷。</summary>
    public Brush Fill { get; }
}
