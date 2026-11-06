using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 倒计时服务：单次倒计时，挂在全局 Ticker 上，每秒推进；到点提示并自动结束。
/// </summary>
public sealed class CountdownService : ICountdownService, IDisposable
{
    private readonly ITicker _ticker;
    private readonly INotificationService _notification;

    private bool _running;
    private TimeSpan _remaining = TimeSpan.Zero;
    private int _totalMinutes;

    /// <summary>构造倒计时服务。</summary>
    /// <param name="ticker">全局节拍。</param>
    /// <param name="notification">通知服务。</param>
    public CountdownService(ITicker ticker, INotificationService notification)
    {
        _ticker = ticker;
        _notification = notification;
        _ticker.Tick += OnTick;
    }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public TimeSpan Remaining => _remaining;

    /// <inheritdoc />
    public int TotalMinutes => _totalMinutes;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public void Start(int minutes)
    {
        _totalMinutes = Math.Clamp(minutes, 1, 600);
        _remaining = TimeSpan.FromMinutes(_totalMinutes);
        _running = true;
        AppEnvironment.Log("倒计时开始：" + _totalMinutes + " 分钟。");
        _notification.ShowBubble("倒计时 " + _totalMinutes + " 分钟，到点我叫你。", BubbleKind.Info);
        Raise();
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _remaining = TimeSpan.Zero;
        Raise();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_running)
        {
            return;
        }

        _remaining -= TimeSpan.FromSeconds(1);
        if (_remaining > TimeSpan.Zero)
        {
            Raise();
            return;
        }

        _running = false;
        _remaining = TimeSpan.Zero;
        _notification.PlaySound(SoundKind.Strong);
        _notification.ShowBubble("倒计时结束啦，时间到！", BubbleKind.Info, "知道了", null);
        _notification.ShowTrayBalloon("倒计时结束", "设定的 " + _totalMinutes + " 分钟已到。");
        AppEnvironment.Log("倒计时结束。");
        Raise();
    }

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>释放：退订节拍。</summary>
    public void Dispose() => _ticker.Tick -= OnTick;
}

/// <summary>
/// 网络监测：网卡可用性 + 对公共 DNS 的 TCP 探测（1.5 秒超时），每 15 秒一次。
/// </summary>
public sealed class NetworkMonitorService : INetworkMonitor, IDisposable
{
    private readonly DispatcherTimer _timer;

    private bool _online;
    private int _latency = -1;
    private bool _running;

    /// <summary>构造网络监测服务。</summary>
    public NetworkMonitorService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) => await ProbeAsync();
    }

    /// <inheritdoc />
    public bool IsOnline => _online;

    /// <inheritdoc />
    public int LatencyMs => _latency;

    /// <inheritdoc />
    public string StatusText => _online ? "在线 · " + _latency + "ms" : "离线";

    /// <inheritdoc />
    public event EventHandler? StatusChanged;

    /// <inheritdoc />
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _timer.Start();
        _ = ProbeAsync();
    }

    /// <inheritdoc />
    public void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    /// <summary>探测一次连通性。</summary>
    private async Task ProbeAsync()
    {
        bool online = false;
        int latency = -1;

        try
        {
            if (NetworkInterface.GetIsNetworkAvailable())
            {
                using TcpClient client = new TcpClient();
                DateTime started = DateTime.UtcNow;
                Task connect = client.ConnectAsync("223.5.5.5", 443);
                Task completed = await Task.WhenAny(connect, Task.Delay(1500));
                if (completed == connect && client.Connected)
                {
                    online = true;
                    latency = (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds);
                }
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("网络探测失败。", ex);
        }

        bool changed = online != _online || latency != _latency;
        _online = online;
        _latency = latency;

        if (changed)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>释放：停止计时器。</summary>
    public void Dispose() => Stop();
}

/// <summary>
/// 天气服务：调用 Open-Meteo 免费接口（无需 API Key），30 分钟刷新一次。
/// </summary>
public sealed class WeatherService : IWeatherService, IDisposable
{
    private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _timer;

    /// <summary>构造天气服务。</summary>
    /// <param name="settings">设置服务（提供城市经纬度）。</param>
    public WeatherService(ISettingsService settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <inheritdoc />
    public WeatherSnapshot? Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler? Updated;

    /// <inheritdoc />
    public void Start()
    {
        _timer.Start();
        if (_settings.Current.WeatherAutoLocate)
        {
            _ = LocateAsync();
        }
        else
        {
            _ = RefreshAsync();
        }
    }

    /// <inheritdoc />
    public void Stop() => _timer.Stop();

    /// <inheritdoc />
    public async Task LocateAsync()
    {
        AppSettings settings = _settings.Current;
        if (!settings.WeatherEnabled)
        {
            return;
        }

        (string city, double lat, double lon)? located = await ResolveLocationAsync();
        if (located is not null)
        {
            AppSettings draft = _settings.Current.Clone();
            draft.WeatherCity = located.Value.city;
            draft.WeatherLatitude = located.Value.lat;
            draft.WeatherLongitude = located.Value.lon;
            _settings.TryUpdate(draft);
            AppEnvironment.Log("IP 定位成功：" + located.Value.city + " (" +
                located.Value.lat.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                located.Value.lon.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
        else
        {
            AppEnvironment.Log("IP 定位失败，沿用已有坐标。");
        }

        await RefreshAsync();
    }

    /// <summary>通过公网 IP 解析地理位置（依次尝试多个免费接口，均无需 Key）。</summary>
    private static async Task<(string city, double lat, double lon)?> ResolveLocationAsync()
    {
        string[] urls =
        {
            "https://ipwho.is/",
            "https://ipapi.co/json/",
            "http://ip-api.com/json/?lang=zh-CN"
        };

        foreach (string url in urls)
        {
            try
            {
                string json = await Client.GetStringAsync(url);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                string city = root.TryGetProperty("city", out JsonElement cityEl) && cityEl.ValueKind == JsonValueKind.String
                    ? cityEl.GetString() ?? string.Empty
                    : string.Empty;

                double lat = 0;
                double lon = 0;
                if (root.TryGetProperty("latitude", out JsonElement latEl))
                {
                    lat = latEl.GetDouble();
                }

                if (root.TryGetProperty("longitude", out JsonElement lonEl))
                {
                    lon = lonEl.GetDouble();
                }

                if (lat is > -90 and < 90 && lon is > -180 and < 180 && (lat != 0 || lon != 0))
                {
                    return (LocalizeCity(city), lat, lon);
                }
            }
            catch (Exception ex)
            {
                AppEnvironment.Log("IP 定位接口失败：" + url, ex);
            }
        }

        return null;
    }

    /// <summary>常见城市英文名转中文（找不到则原样返回）。</summary>
    private static string LocalizeCity(string city)
    {
        return city.Trim() switch
        {
            "Chengdu" => "成都",
            "Beijing" => "北京",
            "Shanghai" => "上海",
            "Shenzhen" => "深圳",
            "Guangzhou" => "广州",
            "Hangzhou" => "杭州",
            "Nanjing" => "南京",
            "Wuhan" => "武汉",
            "Xian" or "Xi'an" => "西安",
            "Chongqing" => "重庆",
            "Tianjin" => "天津",
            "Suzhou" => "苏州",
            "Fuzhou" => "福州",
            "Xiamen" => "厦门",
            "Qingdao" => "青岛",
            "Dalian" => "大连",
            "Changsha" => "长沙",
            "Zhengzhou" => "郑州",
            "Hefei" => "合肥",
            "Kunming" => "昆明",
            "Harbin" => "哈尔滨",
            "Shenyang" => "沈阳",
            _ => city.Trim()
        };
    }

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        AppSettings settings = _settings.Current;
        if (!settings.WeatherEnabled)
        {
            return;
        }

        try
        {
            string url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
                "&current=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                "&timezone=auto&forecast_days=3",
                settings.WeatherLatitude,
                settings.WeatherLongitude);

            string json = await Client.GetStringAsync(url);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            JsonElement current = root.GetProperty("current");
            double temperature = current.GetProperty("temperature_2m").GetDouble();
            int code = current.GetProperty("weather_code").GetInt32();

            List<DailyForecast> days = new List<DailyForecast>();
            JsonElement daily = root.GetProperty("daily");
            JsonElement codes = daily.GetProperty("weather_code");
            JsonElement max = daily.GetProperty("temperature_2m_max");
            JsonElement min = daily.GetProperty("temperature_2m_min");
            JsonElement times = daily.GetProperty("time");

            for (int i = 0; i < times.GetArrayLength() && i < 3; i++)
            {
                int dayCode = codes[i].GetInt32();
                days.Add(new DailyForecast(
                    DateOnly.Parse(times[i].GetString() ?? DateTime.Today.ToString("yyyy-MM-dd")),
                    min[i].GetDouble(),
                    max[i].GetDouble(),
                    Describe(dayCode),
                    Glyph(dayCode)));
            }

            Current = new WeatherSnapshot(
                settings.WeatherCity,
                Math.Round(temperature, 1),
                Describe(code),
                Glyph(code),
                DateTimeOffset.Now,
                days);

            AppEnvironment.Log("天气已更新：" + Current.City + " " + Current.Temperature + "°C " + Current.Description);
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 网络波动时不打扰用户，保留上一次成功的数据。
            AppEnvironment.Log("刷新天气失败。", ex);
        }
    }

    /// <summary>WMO 天气代码 → 中文描述。</summary>
    private static string Describe(int code) => code switch
    {
        0 => "晴",
        1 => "少云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "冻雨",
        71 or 73 or 75 => "雪",
        77 => "米雪",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷雨冰雹",
        _ => "未知"
    };

    /// <summary>WMO 天气代码 → Segoe MDL2 图标。</summary>
    private static string Glyph(int code) => code switch
    {
        0 or 1 => "\uE706",
        2 or 3 => "\uE753",
        45 or 48 => "\uE9CA",
        71 or 73 or 75 or 77 or 85 or 86 => "\uE9C8",
        95 or 96 or 99 => "\uE9C6",
        _ => "\uE9C4"
    };

    /// <summary>释放：停止刷新。</summary>
    public void Dispose() => Stop();
}

/// <summary>
/// 剪贴板历史：每 700ms 轮询剪贴板文本，去重后保留最近 N 条，落盘为 JSON。
/// </summary>
public sealed class ClipboardHistoryService : IClipboardHistoryService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _timer;
    private readonly List<ClipboardEntry> _entries = new List<ClipboardEntry>();

    private bool _running;
    private string _lastText = string.Empty;

    /// <summary>构造剪贴板历史服务。</summary>
    /// <param name="settings">设置服务（开关与条数上限）。</param>
    public ClipboardHistoryService(ISettingsService settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Poll();
        Load();
    }

    /// <inheritdoc />
    public IReadOnlyList<ClipboardEntry> Entries => _entries;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>历史文件路径。</summary>
    private static string FilePath => Path.Combine(AppEnvironment.RootDirectory, "clipboard-history.json");

    /// <inheritdoc />
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _timer.Start();
    }

    /// <inheritdoc />
    public void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    /// <inheritdoc />
    public void Clear()
    {
        _entries.Clear();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Remove(Guid id)
    {
        int removed = _entries.RemoveAll(e => e.Id == id);
        if (removed > 0)
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>把文本写入剪贴板（供历史窗口一键复制）。</summary>
    /// <param name="text">文本。</param>
    /// <returns>是否成功。</returns>
    public static bool TrySetText(string text)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == 2)
                {
                    AppEnvironment.Log("写入剪贴板失败。", ex);
                    return false;
                }

                System.Threading.Thread.Sleep(50);
            }
        }

        return false;
    }

    private void Poll()
    {
        if (!_settings.Current.ClipboardHistoryEnabled)
        {
            return;
        }

        string text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            text = Clipboard.GetText();
        }
        catch (Exception)
        {
            // 剪贴板被其他进程占用时跳过这一轮即可。
            return;
        }

        string content = (text ?? string.Empty).Trim();
        if (content.Length == 0 || content.Length > 20000 || content == _lastText)
        {
            return;
        }

        _lastText = content;

        // 与最新一条相同则不重复记录（例如用户复制了历史里的条目）。
        if (_entries.Count > 0 && _entries[0].Text == content)
        {
            return;
        }

        _entries.Insert(0, new ClipboardEntry(Guid.NewGuid(), content, DateTimeOffset.Now));
        Trim();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Trim()
    {
        int max = Math.Clamp(_settings.Current.ClipboardHistoryMax, 10, 500);
        while (_entries.Count > max)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            string json = File.ReadAllText(FilePath);
            List<ClipboardEntry>? items = JsonSerializer.Deserialize<List<ClipboardEntry>>(json);
            if (items is null)
            {
                return;
            }

            _entries.Clear();
            _entries.AddRange(items.Where(i => !string.IsNullOrWhiteSpace(i.Text)));
            Trim();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("读取剪贴板历史失败。", ex);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppEnvironment.RootDirectory);
            string json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("保存剪贴板历史失败。", ex);
        }
    }

    /// <summary>释放：停止轮询。</summary>
    public void Dispose() => Stop();
}
