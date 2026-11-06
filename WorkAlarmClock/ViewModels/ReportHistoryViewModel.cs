using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.ViewModels;

/// <summary>历史日报列表项。</summary>
public sealed class ReportHistoryItem
{
    /// <summary>日期。</summary>
    public DateOnly Date { get; init; }

    /// <summary>日期文案（11.04）。</summary>
    public string DateText { get; init; } = string.Empty;

    /// <summary>星期文案。</summary>
    public string WeekText { get; init; } = string.Empty;

    /// <summary>摘要（工时 · 事项数）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>日报文件路径（可能已不存在）。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>文件内容（读取失败为空）。</summary>
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// 历史日报视图模型：按日期列出已生成的日报，支持关键字过滤、预览与一键生成周报。
/// </summary>
public sealed class ReportHistoryViewModel : ObservableObject
{
    private readonly List<ReportHistoryItem> _all = new List<ReportHistoryItem>();

    private string _searchText = string.Empty;
    private string _previewText = string.Empty;
    private string _statusText = string.Empty;
    private ReportHistoryItem? _selectedItem;

    /// <summary>构造视图模型并加载历史列表。</summary>
    public ReportHistoryViewModel()
    {
        Items = new ObservableCollection<ReportHistoryItem>();

        RefreshCommand = new RelayCommand(LoadHistory);
        GenerateWeeklyCommand = new RelayCommand(GenerateWeekly);
        CopyCommand = new RelayCommand(CopyPreview);
        OpenFileCommand = new RelayCommand(OpenSelectedFile);
        OpenFolderCommand = new RelayCommand(OpenReportFolder);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        LoadHistory();
    }

    /// <summary>过滤后的日报列表。</summary>
    public ObservableCollection<ReportHistoryItem> Items { get; }

    /// <summary>搜索框文本（按日期或内容过滤）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>当前选中项。</summary>
    public ReportHistoryItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                PreviewText = value?.Content ?? string.Empty;
                StatusText = value is null
                    ? string.Empty
                    : (string.IsNullOrEmpty(value.Content) ? "文件不存在或为空：" + value.FilePath : value.FilePath);
            }
        }
    }

    /// <summary>预览文本。</summary>
    public string PreviewText
    {
        get => _previewText;
        private set => SetProperty(ref _previewText, value);
    }

    /// <summary>底部状态文案。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>刷新列表命令。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>生成本周周报命令。</summary>
    public RelayCommand GenerateWeeklyCommand { get; }

    /// <summary>复制预览内容命令。</summary>
    public RelayCommand CopyCommand { get; }

    /// <summary>打开当前日报文件命令。</summary>
    public RelayCommand OpenFileCommand { get; }

    /// <summary>打开日报目录命令。</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>关闭窗口命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>加载历史日报（含文件内容缓存）。</summary>
    private void LoadHistory()
    {
        try
        {
            _all.Clear();
            IReadOnlyList<DateOnly> dates = AppServices.Storage.ListReportDates(200);

            foreach (DateOnly date in dates)
            {
                DayRecord? record = AppServices.Storage.LoadDay(date);
                ReportRecord? report = record?.Reports.LastOrDefault();

                // 优先当前命名规则下的文件（yyyy.MM.dd工作日报.*），其次回退到记录里保存的路径。
                string current = AppServices.Report.GetReportPath(date);
                string path = File.Exists(current)
                    ? current
                    : (report?.FilePath ?? current);

                string content = string.Empty;
                try
                {
                    if (File.Exists(path))
                    {
                        content = File.ReadAllText(path);
                    }
                }
                catch (Exception ex)
                {
                    AppEnvironment.Log("读取历史日报失败：" + path, ex);
                }

                int taskCount = record?.Tasks.Count ?? 0;
                _all.Add(new ReportHistoryItem
                {
                    Date = date,
                    DateText = date.ToString("MM.dd", CultureInfo.InvariantCulture),
                    WeekText = WeekName(date.DayOfWeek),
                    Summary = taskCount + " 条事项",
                    FilePath = path,
                    Content = content
                });
            }

            ApplyFilter();
            StatusText = _all.Count == 0
                ? "还没有生成过日报，先去「日报预览」生成一份吧。"
                : "共 " + _all.Count.ToString(CultureInfo.InvariantCulture) + " 份日报（最多显示最近 200 天）";
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("加载历史日报失败。", ex);
            StatusText = "加载失败：" + ex.Message;
        }
    }

    /// <summary>按搜索词过滤（日期 / 星期 / 正文）。</summary>
    private void ApplyFilter()
    {
        string query = (_searchText ?? string.Empty).Trim();
        Items.Clear();

        IEnumerable<ReportHistoryItem> source = _all;
        if (query.Length > 0)
        {
            source = _all.Where(item =>
                item.DateText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.WeekText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Date.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (ReportHistoryItem item in source)
        {
            Items.Add(item);
        }

        SelectedItem = Items.FirstOrDefault();
    }

    /// <summary>生成本周周报，并把内容显示在预览区。</summary>
    private void GenerateWeekly()
    {
        try
        {
            DateOnly today = DateOnly.FromDateTime(AppServices.Ticker.Now.LocalDateTime);
            WeeklyReport report = AppServices.Report.GenerateWeekly(today);
            PreviewText = report.Content;
            StatusText = "周报已生成：" + report.FilePath;
            AppServices.Notification.ShowBubble(
                "周报已生成：" + Path.GetFileName(report.FilePath),
                BubbleKind.Info,
                "打开目录",
                OpenReportFolder);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("生成周报失败。", ex);
            StatusText = "生成周报失败：" + ex.Message;
        }
    }

    private void CopyPreview()
    {
        if (string.IsNullOrWhiteSpace(PreviewText))
        {
            return;
        }

        bool ok = AppServices.Report.CopyToClipboard(PreviewText);
        StatusText = ok ? "已复制到剪贴板。" : "复制失败，请稍后重试。";
    }

    private void OpenSelectedFile()
    {
        if (_selectedItem is null)
        {
            return;
        }

        OpenPath(_selectedItem.FilePath);
    }

    private void OpenReportFolder() => OpenPath(AppServices.Report.ResolveReportDirectory());

    private static void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process? process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            process?.Dispose();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("打开路径失败：" + path, ex);
        }
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
