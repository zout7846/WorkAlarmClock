using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.ViewModels;

/// <summary>
/// 剪贴板历史视图模型：列出历史条目，支持搜索、复制、删除与清空。
/// </summary>
public sealed class ClipboardHistoryViewModel : ObservableObject
{
    private readonly List<ClipboardEntry> _all = new List<ClipboardEntry>();

    private string _searchText = string.Empty;
    private string _statusText = string.Empty;

    /// <summary>构造视图模型并订阅历史变化。</summary>
    public ClipboardHistoryViewModel()
    {
        Items = new ObservableCollection<ClipboardEntry>();

        RefreshCommand = new RelayCommand(Reload);
        CopyCommand = new RelayCommand(CopyEntry);
        RemoveCommand = new RelayCommand(RemoveEntry);
        ClearCommand = new RelayCommand(ClearAll);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        AppServices.ClipboardHistory.Changed += OnHistoryChanged;
        Reload();
    }

    /// <summary>过滤后的条目。</summary>
    public ObservableCollection<ClipboardEntry> Items { get; }

    /// <summary>搜索文本（按正文过滤）。</summary>
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

    /// <summary>底部状态文案。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>刷新命令。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>复制某条命令（参数为 ClipboardEntry）。</summary>
    public RelayCommand CopyCommand { get; }

    /// <summary>删除某条命令（参数为 ClipboardEntry）。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>清空命令。</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>关闭命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>退出订阅（窗口关闭时调用）。</summary>
    public void Detach() => AppServices.ClipboardHistory.Changed -= OnHistoryChanged;

    private void OnHistoryChanged(object? sender, EventArgs e) => Reload();

    private void Reload()
    {
        _all.Clear();
        _all.AddRange(AppServices.ClipboardHistory.Entries);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = (_searchText ?? string.Empty).Trim();
        IEnumerable<ClipboardEntry> source = _all;
        if (query.Length > 0)
        {
            source = _all.Where(e => e.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        Items.Clear();
        foreach (ClipboardEntry entry in source)
        {
            Items.Add(entry);
        }

        StatusText = _all.Count == 0
            ? "还没有剪贴板历史，复制一段文字试试。"
            : "共 " + _all.Count.ToString(CultureInfo.InvariantCulture) + " 条" +
              (query.Length > 0 ? "（过滤后 " + Items.Count.ToString(CultureInfo.InvariantCulture) + " 条）" : string.Empty);
    }

    private void CopyEntry(object? parameter)
    {
        if (parameter is not ClipboardEntry entry)
        {
            return;
        }

        if (ClipboardHistoryService.TrySetText(entry.Text))
        {
            StatusText = "已复制到剪贴板。";
            AppServices.Notification.ShowBubble("已复制到剪贴板。", BubbleKind.Info);
        }
        else
        {
            StatusText = "复制失败，请稍后重试。";
        }
    }

    private void RemoveEntry(object? parameter)
    {
        if (parameter is not ClipboardEntry entry)
        {
            return;
        }

        AppServices.ClipboardHistory.Remove(entry.Id);
        Reload();
    }

    private void ClearAll()
    {
        MessageBoxResult result = MessageBox.Show(
            "确定清空剪贴板历史吗？此操作不可恢复。",
            "提示",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            AppServices.ClipboardHistory.Clear();
            Reload();
        }
    }
}
