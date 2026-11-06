using System;
using System.Collections.ObjectModel;
using System.Globalization;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.ViewModels;

/// <summary>单日预报展示项。</summary>
public sealed class DayForecastItem
{
    /// <summary>星期文案。</summary>
    public string WeekdayText { get; init; } = string.Empty;

    /// <summary>图标字符。</summary>
    public string IconGlyph { get; init; } = string.Empty;

    /// <summary>温度区间（21°~29°）。</summary>
    public string RangeText { get; init; } = string.Empty;

    /// <summary>天气描述。</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// 天气视图模型：展示当前天气与三日预报，支持手动刷新。
/// </summary>
public sealed class WeatherViewModel : ObservableObject
{
    private string _cityText = "加载中…";
    private string _temperatureText = "--";
    private string _descriptionText = string.Empty;
    private string _iconGlyph = "\uE706";
    private string _updatedText = string.Empty;

    /// <summary>构造视图模型并订阅天气更新。</summary>
    public WeatherViewModel()
    {
        Days = new ObservableCollection<DayForecastItem>();
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        LocateCommand = new RelayCommand(() => _ = LocateAsync());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        AppServices.Weather.Updated += OnUpdated;
        OnUpdated(this, EventArgs.Empty);
        _ = RefreshAsync();
    }

    /// <summary>城市名。</summary>
    public string CityText
    {
        get => _cityText;
        private set => SetProperty(ref _cityText, value);
    }

    /// <summary>当前温度。</summary>
    public string TemperatureText
    {
        get => _temperatureText;
        private set => SetProperty(ref _temperatureText, value);
    }

    /// <summary>天气描述。</summary>
    public string DescriptionText
    {
        get => _descriptionText;
        private set => SetProperty(ref _descriptionText, value);
    }

    /// <summary>图标字符。</summary>
    public string IconGlyph
    {
        get => _iconGlyph;
        private set => SetProperty(ref _iconGlyph, value);
    }

    /// <summary>更新时间文案。</summary>
    public string UpdatedText
    {
        get => _updatedText;
        private set => SetProperty(ref _updatedText, value);
    }

    /// <summary>三日预报。</summary>
    public ObservableCollection<DayForecastItem> Days { get; }

    /// <summary>刷新命令。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>按 IP 自动定位命令。</summary>
    public RelayCommand LocateCommand { get; }

    /// <summary>关闭命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>退出订阅（窗口关闭时调用）。</summary>
    public void Detach() => AppServices.Weather.Updated -= OnUpdated;

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        await AppServices.Weather.RefreshAsync();
    }

    private async System.Threading.Tasks.Task LocateAsync()
    {
        CityText = "定位中…";
        DescriptionText = "正在按 IP 解析位置";
        await AppServices.Weather.LocateAsync();
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        WeatherSnapshot? snapshot = AppServices.Weather.Current;
        if (snapshot is null)
        {
            CityText = "暂无数据";
            TemperatureText = "--";
            DescriptionText = "请稍后刷新重试";
            IconGlyph = "\uE753";
            UpdatedText = string.Empty;
            Days.Clear();
            return;
        }

        CityText = snapshot.City;
        TemperatureText = snapshot.Temperature.ToString("0.#", CultureInfo.InvariantCulture) + "°";
        DescriptionText = snapshot.Description;
        IconGlyph = snapshot.IconGlyph;
        UpdatedText = "更新于 " + snapshot.UpdatedAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

        Days.Clear();
        foreach (DailyForecast day in snapshot.Days)
        {
            Days.Add(new DayForecastItem
            {
                WeekdayText = WeekName(day.Date.DayOfWeek),
                IconGlyph = day.IconGlyph,
                RangeText = day.Min.ToString("0", CultureInfo.InvariantCulture) + "°~" +
                            day.Max.ToString("0", CultureInfo.InvariantCulture) + "°",
                Description = day.Description
            });
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
