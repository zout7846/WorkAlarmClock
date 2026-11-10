using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using WorkAlarmClock.Models;
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
    public IReadOnlyList<CityPreset> CityOptions => CityPresets.All;
    private CityPreset? _selectedCity;
    public CityPreset? SelectedCity { get => _selectedCity; set => SetProperty(ref _selectedCity, value); }
    public RelayCommand ApplyCityCommand { get; }
    public string LocationText => (AppServices.Settings.Current.WeatherManualLocation ? "手动地区 · " : "IP 近似定位 · ") + AppServices.Settings.Current.WeatherCity;
    private bool _isBusy;
    private string _statusText = string.Empty;
    public bool IsIdle => !_isBusy;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public ObservableCollection<HourForecastItem> Hours { get; } = new();
    public ObservableCollection<double> RainTrend { get; } = new();
    public ObservableCollection<string> HourLabels { get; } = new();
    private string _cityText = "加载中…";
    private string _temperatureText = "--";
    private string _descriptionText = string.Empty;
    private string _iconGlyph = "\uE706";
    private string _updatedText = string.Empty;

    /// <summary>构造视图模型并订阅天气更新。</summary>
    public WeatherViewModel()
    {
        Days = new ObservableCollection<DayForecastItem>();
        SelectedCity = CityOptions.FirstOrDefault(c => c.Name == AppServices.Settings.Current.WeatherCity);
        ApplyCityCommand = new RelayCommand(() => _ = ApplyCityAsync());
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
        if (_isBusy) return;
        SetBusy(true);
        try { await AppServices.Weather.RefreshAsync(); }
        finally { SetBusy(false); }
    }

    private async System.Threading.Tasks.Task LocateAsync()
    {
        if (_isBusy) return;
        SetBusy(true);
        StatusText = "正在按 IP 解析位置…";
        try { await AppServices.Weather.LocateAsync(WeatherLocateTrigger.Manual); }
        finally { SetBusy(false); }
    }

    private async System.Threading.Tasks.Task ApplyCityAsync()
    {
        if (_isBusy) return;
        if (SelectedCity is not {} city) { StatusText = "请从列表选择城市后应用"; return; }
        SetBusy(true);
        try
        {
            var draft = AppServices.Settings.Current.Clone();
            draft.WeatherCity = city.Name;
            draft.WeatherLatitude = city.Latitude;
            draft.WeatherLongitude = city.Longitude;
            draft.WeatherManualLocation = true;
            draft.WeatherAutoLocate = false;
            draft.WeatherLocatedAt = null;
            var result = AppServices.Settings.TryUpdate(draft);
            if (!result.Ok) { StatusText = result.Error ?? "地区保存失败"; return; }
            OnPropertyChanged(nameof(LocationText));
            await AppServices.Weather.RefreshAsync();
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        OnPropertyChanged(nameof(IsIdle));
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        StatusText = AppServices.Weather.StatusText;
        OnPropertyChanged(nameof(LocationText));
        WeatherSnapshot? snapshot = AppServices.Weather.Current;
        if (snapshot is null || snapshot.City != AppServices.Settings.Current.WeatherCity)
        {
            CityText = AppServices.Settings.Current.WeatherCity;
            TemperatureText = "--";
            DescriptionText = "请稍后刷新重试";
            IconGlyph = "\uE753";
            UpdatedText = string.Empty;
            Days.Clear();
            Hours.Clear();
            RainTrend.Clear(); HourLabels.Clear();
            return;
        }

        CityText = snapshot.City;
        TemperatureText = snapshot.Temperature.ToString("0.#", CultureInfo.InvariantCulture) + "°";
        DescriptionText = snapshot.Description;
        IconGlyph = snapshot.IconGlyph;
        UpdatedText = "获取 " + snapshot.UpdatedAt.LocalDateTime.ToString("HH:mm:ss") +
            (snapshot.ObservedAt is {} observed ? " · 天气时刻 " + observed.ToString("MM-dd HH:mm") : "");
        Hours.Clear();
        RainTrend.Clear(); HourLabels.Clear();
        foreach (var hour in snapshot.Hours)
        {
            // Missing probabilities remain absent from the trend rather than looking like 0%.
            if (hour.Probability is {} probability)
            {
                RainTrend.Add(Math.Clamp(probability, 0, 100));
                HourLabels.Add(hour.Time.ToString("HH:mm"));
            }
        }
        foreach (var hour in snapshot.Hours)
            Hours.Add(new HourForecastItem(
                hour.Time.ToString("HH:mm"), hour.Temperature.ToString("0") + "°",
                hour.Probability is {} probability ? probability.ToString("0") + "%" : "—",
                hour.Precipitation is {} rain ? rain.ToString("0.#") + " mm" : "—",
                hour.Probability is {} p ? Math.Clamp(p, 0, 100) : 0));

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

public sealed record HourForecastItem(string Time, string Temperature, string Probability, string Rain, double BarHeight);
