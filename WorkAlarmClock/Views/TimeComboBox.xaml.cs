using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace WorkAlarmClock.Views;

/// <summary>
/// 零依赖时间下拉框：按指定粒度（默认 30 分钟）列出日内时刻，双向绑定 <see cref="TimeOnly"/>?。
/// 用于设置页全部时间输入（R-24），避免引入第三方日期控件。
/// </summary>
public partial class TimeComboBox : UserControl
{
    /// <summary>选中时刻（TimeOnly?，可空以支持未设置态）。</summary>
    public static readonly DependencyProperty SelectedTimeProperty =
        DependencyProperty.Register(
            nameof(SelectedTime),
            typeof(TimeOnly?),
            typeof(TimeComboBox),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedTimeChanged));

    /// <summary>粒度（分钟，默认 30）。</summary>
    public static readonly DependencyProperty GranularityMinutesProperty =
        DependencyProperty.Register(
            nameof(GranularityMinutes),
            typeof(int),
            typeof(TimeComboBox),
            new PropertyMetadata(30, OnGranularityChanged));

    private readonly ObservableCollection<TimeOnly> _items = new ObservableCollection<TimeOnly>();
    private bool _suppressChange;

    /// <summary>构造时间下拉框并加载时刻列表。</summary>
    public TimeComboBox()
    {
        InitializeComponent();
        TimeSelector.ItemsSource = _items;
        RebuildItems();
        CoerceSelected();
    }

    /// <summary>选中时刻（双向绑定目标）。</summary>
    public TimeOnly? SelectedTime
    {
        get => (TimeOnly?)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    /// <summary>粒度（分钟）。</summary>
    public int GranularityMinutes
    {
        get => (int)GetValue(GranularityMinutesProperty);
        set => SetValue(GranularityMinutesProperty, value);
    }

    private static void OnGranularityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimeComboBox)d).RebuildItems();

    private static void OnSelectedTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TimeComboBox)d).CoerceSelected();

    /// <summary>按粒度重建时刻列表（00:00 起，保证不跨日）。</summary>
    private void RebuildItems()
    {
        int step = Math.Max(1, GranularityMinutes);
        _items.Clear();
        for (int total = 0; total + step <= 1440; total += step)
        {
            _items.Add(new TimeOnly(total / 60, total % 60));
        }

        CoerceSelected();
    }

    /// <summary>根据当前 SelectedTime 同步下拉框选中项（无匹配则清空）。</summary>
    private void CoerceSelected()
    {
        TimeOnly? value = SelectedTime;
        _suppressChange = true;
        try
        {
            if (value is null)
            {
                TimeSelector.SelectedItem = null;
                return;
            }

            foreach (TimeOnly candidate in _items)
            {
                if (candidate == value.Value)
                {
                    TimeSelector.SelectedItem = candidate;
                    return;
                }
            }

            // 非整粒度时刻：暂无对应项，留空待用户重选。
            TimeSelector.SelectedItem = null;
        }
        finally
        {
            _suppressChange = false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChange)
        {
            return;
        }

        SelectedTime = TimeSelector.SelectedItem is TimeOnly picked ? picked : (TimeOnly?)null;
    }
}
