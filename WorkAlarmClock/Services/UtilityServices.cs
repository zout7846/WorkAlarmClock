using System;
using System.Diagnostics;
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
    private static readonly TimeSpan WeatherRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly HttpClient LocationClient = CreateLocationClient();
    private static readonly TimeSpan LocationTimeout = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan LocationCacheTtl = TimeSpan.FromHours(6);

    private static HttpClient CreateLocationClient()
    {
        HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WorkAlarmClock/2.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private CancellationTokenSource? _refreshCancellation;
    private Task? _locating;
    public string StatusText { get; private set; } = string.Empty;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _timer;

    /// <summary>构造天气服务。</summary>
    /// <param name="settings">设置服务（提供城市经纬度）。</param>
    public WeatherService(ISettingsService settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = WeatherRefreshInterval };
        _timer.Tick += async (_, _) => await LocateAsync(WeatherLocateTrigger.Periodic);
    }

    /// <inheritdoc />
    public WeatherSnapshot? Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler? Updated;

    /// <inheritdoc />
    public void Start()
    {
        _timer.Start();
        if (_settings.Current.WeatherManualLocation)
        {
            AppEnvironment.Log("已启用手动地区：" + _settings.Current.WeatherCity + "，跳过 IP 定位。");
            _ = RefreshAsync();
        }
        else if (_settings.Current.WeatherAutoLocate)
        {
            _ = LocateAsync(WeatherLocateTrigger.Startup);
        }
        else
        {
            _ = RefreshAsync();
        }
    }

    /// <inheritdoc />
    public void Stop() => _timer.Stop();

    /// <inheritdoc />
    public Task LocateAsync(WeatherLocateTrigger trigger = WeatherLocateTrigger.Startup)
    {
        if (_locating is { IsCompleted: false }) return _locating;
        return _locating = LocateCoreAsync(trigger);
    }

    private async Task LocateCoreAsync(WeatherLocateTrigger trigger)
    {
        AppSettings settings = _settings.Current;
        if (!settings.WeatherEnabled)
        {
            return;
        }

        if (settings.WeatherManualLocation && trigger != WeatherLocateTrigger.Manual)
        {
            AppEnvironment.Log("已启用手动地区，跳过 IP 定位。");
            await RefreshAsync();
            return;
        }

        if (trigger != WeatherLocateTrigger.Manual && (!settings.WeatherAutoLocate || HasFreshLocation(settings)))
        {
            AppEnvironment.Log("IP 定位缓存仍有效，跳过本次联网定位。");
            await RefreshAsync();
            return;
        }

        LocationResolution? located = await ResolveLocationAsync();
        // A manual change made while probes were running always wins.
        if (_settings.Current.WeatherManualLocation != settings.WeatherManualLocation ||
            _settings.Current.WeatherCity != settings.WeatherCity ||
            _settings.Current.WeatherLatitude != settings.WeatherLatitude ||
            _settings.Current.WeatherLongitude != settings.WeatherLongitude)
        {
            await RefreshAsync();
            return;
        }
        if (located is not null && !located.AnyDomestic)
        {
            AppEnvironment.Log(trigger == WeatherLocateTrigger.Manual
                ? "按 IP 重新定位得到境外结果（很可能是应用走了代理/VPN），已保留原有地区设置；如需切换请在设置中手动指定地区。"
                : "IP 定位结果均判定为境外（很可能是应用走了代理/VPN），为保护已有地区设置，本次不写回城市与坐标；如需更新请在设置中手动指定地区。");
            await RefreshAsync();
            StatusText += " · IP 返回境外位置，已保留原地区，请检查代理或手动选城市";
            Updated?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (located is not null && ValidCoords(located.Lat, located.Lon))
        {
            bool coordinatesUnchanged = !ValidCoords(located.Lat, located.Lon)
                || (Math.Abs(settings.WeatherLatitude - located.Lat) < 0.0001
                    && Math.Abs(settings.WeatherLongitude - located.Lon) < 0.0001);

            if (trigger == WeatherLocateTrigger.Periodic
                && !settings.WeatherManualLocation
                && string.Equals(settings.WeatherCity, located.City, StringComparison.Ordinal)
                && coordinatesUnchanged)
            {
                AppEnvironment.Log("IP 定位结果无变化，跳过写回设置。");
            }
            else
            {
                AppSettings draft = _settings.Current.Clone();
                draft.WeatherCity = located.City;
                if (ValidCoords(located.Lat, located.Lon))
                {
                    draft.WeatherLatitude = located.Lat;
                    draft.WeatherLongitude = located.Lon;
                }

                draft.WeatherLocatedAt = DateTimeOffset.Now;
                if (trigger == WeatherLocateTrigger.Manual)
                {
                    draft.WeatherManualLocation = false;
                    draft.WeatherAutoLocate = true;
                }

                var saved = _settings.TryUpdate(draft);
                if (!saved.Ok)
                {
                    StatusText = saved.Error ?? "定位地区保存失败";
                    Updated?.Invoke(this, EventArgs.Empty);
                    return;
                }
                AppEnvironment.Log("IP 定位成功：" + located.City + " (" +
                    located.Lat.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                    located.Lon.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ")，来源：IP 多源合并");
            }
        }
        else
        {
            AppEnvironment.Log("IP 定位失败，沿用已有坐标。");
        }

        await RefreshAsync();
        if (located is null || !ValidCoords(located.Lat, located.Lon))
        {
            StatusText += " · 定位未取得可靠城市坐标，已保留原地区";
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>近期是否成功定位过（用于跳过重复的联网定位）。</summary>
    private static bool HasFreshLocation(AppSettings s)
    {
        if (s.WeatherLocatedAt is not DateTimeOffset locatedAt)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        return locatedAt >= now - LocationCacheTtl
            && locatedAt <= now
            && ValidCoords(s.WeatherLatitude, s.WeatherLongitude)
            && !string.IsNullOrWhiteSpace(s.WeatherCity);
    }

    /// <summary>通过公网 IP 并发解析地理位置（多源合并，均无需 Key）。</summary>
    private static async Task<LocationResolution?> ResolveLocationAsync()
    {
        using CancellationTokenSource cts = new CancellationTokenSource(LocationTimeout);
        Stopwatch sw = Stopwatch.StartNew();
        Task<IpProbe?>[] tasks =
        {
            SafeProbeAsync("ipwho.is", ProbeIpWhoIsAsync, cts.Token),
            SafeProbeAsync("ipip.net", ProbeIpIpNetAsync, cts.Token),
            SafeProbeAsync("ip-api.com", ProbeIpApiAsync, cts.Token)
        };
        IpProbe?[] all = await Task.WhenAll(tasks);
        List<IpProbe> probes = all.Where(p => p is not null).Select(p => p!).ToList();
        sw.Stop();
        LocationResolution? merged = MergeLocation(probes);
        AppEnvironment.Log(merged is null
            ? $"IP 定位失败：无可用数据源（用时 {sw.ElapsedMilliseconds}ms），沿用已有坐标。"
            : $"IP 定位完成：{merged.City} ({merged.Lat:0.####}, {merged.Lon:0.####})，用时 {sw.ElapsedMilliseconds}ms");
        return merged;
    }

    /// <summary>合并多个定位源：境内中文城市优先、境内坐标优先、坐标与城市不一致时舍弃境外坐标。</summary>
    private static LocationResolution? MergeLocation(IReadOnlyList<IpProbe> probes)
    {
        if (probes.Count == 0)
        {
            return null;
        }

        // Vote on normalized city names, never combine different cities' coordinates.
        var candidates = probes.Where(p => p.Domestic && !string.IsNullOrWhiteSpace(p.City))
            .GroupBy(p => NormalizeCity(LocalizeCity(p.City)), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ToList();
        if (candidates.Count == 0) return null;
        var winner = candidates[0];
        if (candidates.Count > 1 && winner.Count() < 2)
        {
            AppEnvironment.Log("IP 定位来源城市不一致，保留原地区，建议手动选择。");
            return null;
        }
        string city = winner.Key;
        CityPreset? preset = CityPresets.All.FirstOrDefault(p => p.Name == city);
        IpProbe? geo = winner.FirstOrDefault(p => p.HasCoordinates);
        double lat = preset?.Latitude ?? geo?.Latitude ?? 0;
        double lon = preset?.Longitude ?? geo?.Longitude ?? 0;
        bool anyDomestic = true;
        return new LocationResolution(city, lat, lon, anyDomestic);
    }

    /// <summary>单个 IP 定位源的探测结果。</summary>
    private sealed record IpProbe(string Source, string City, string Region, string Country,
        string CountryCode, double Latitude, double Longitude, bool HasCoordinates, bool Domestic);

    /// <summary>融合后的定位结果；AnyDomestic 表示本次有效探针中是否至少有一个判定为境内。</summary>
    private sealed record LocationResolution(string City, double Lat, double Lon, bool AnyDomestic);

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)
            ? d
            : 0;

    private static bool ValidCoords(double lat, double lon) =>
        lat is > -90 and < 90 && lon is > -180 and < 180 && (lat != 0 || lon != 0);

    private static bool IsDomestic(string country, string code) =>
        code.Equals("CN", StringComparison.OrdinalIgnoreCase) || country.Contains("中国", StringComparison.Ordinal);

    private static bool HasChinese(string s) => s.Any(c => c is >= '\u4E00' and <= '\u9FFF');

    /// <summary>去掉城市名尾部的行政区后缀（如「成都市」→「成都」）。</summary>
    private static string NormalizeCity(string city)
    {
        string s = (city ?? string.Empty).Trim();
        string[] suffixes = { "特别行政区", "维吾尔自治区", "壮族自治区", "回族自治区", "自治区", "自治州", "地区", "市" };
        foreach (string suffix in suffixes)
        {
            if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.Ordinal))
            {
                s = s.Substring(0, s.Length - suffix.Length);
                break;
            }
        }

        return s.Trim();
    }

    /// <summary>取回原始 JSON 字节并跳过 UTF-8 BOM / 前导空白，避开字符串编码问题。</summary>
    private static async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken ct)
    {
        byte[] bytes = await LocationClient.GetByteArrayAsync(url, ct);
        int start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            start = 3;
        }

        while (start < bytes.Length && IsWhitespace(bytes[start]))
        {
            start++;
        }

        return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, start, bytes.Length - start));
    }

    private static bool IsWhitespace(byte b) => b == 0x20 || b == 0x09 || b == 0x0D || b == 0x0A;

    /// <summary>ipwho.is：顶层 city/region/country/country_code/latitude/longitude，success=false 视为失败。</summary>
    private static async Task<IpProbe?> ProbeIpWhoIsAsync(CancellationToken ct)
    {
        using JsonDocument doc = await FetchJsonAsync("https://ipwho.is/", ct);
        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("success", out JsonElement ok)
            && ok.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        string city = Str(root, "city");
        string region = Str(root, "region");
        string country = Str(root, "country");
        string code = Str(root, "country_code");
        double lat = Num(root, "latitude");
        double lon = Num(root, "longitude");
        bool hasCoords = ValidCoords(lat, lon);
        if (string.IsNullOrWhiteSpace(city) && !hasCoords)
        {
            return null;
        }

        return new IpProbe("ipwho.is", city, region, country, code, lat, lon, hasCoords, IsDomestic(country, code));
    }

    /// <summary>myip.ipip.net：data.location 数组 [国家, 省, 城市, ...]，无经纬度。</summary>
    private static async Task<IpProbe?> ProbeIpIpNetAsync(CancellationToken ct)
    {
        using JsonDocument doc = await FetchJsonAsync("https://myip.ipip.net/json", ct);
        JsonElement root = doc.RootElement;
        if (!string.Equals(Str(root, "ret"), "ok", StringComparison.Ordinal))
        {
            return null;
        }

        if (!root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("location", out JsonElement loc)
            || loc.ValueKind != JsonValueKind.Array
            || loc.GetArrayLength() < 3)
        {
            return null;
        }

        string country = loc[0].ValueKind == JsonValueKind.String ? loc[0].GetString() ?? string.Empty : string.Empty;
        string region = loc[1].ValueKind == JsonValueKind.String ? loc[1].GetString() ?? string.Empty : string.Empty;
        string city = loc[2].ValueKind == JsonValueKind.String ? loc[2].GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(city))
        {
            return null;
        }

        bool domestic = IsDomestic(country, string.Empty);
        return new IpProbe("ipip.net", city, region, country,
            domestic ? "CN" : string.Empty, 0, 0, false, domestic);
    }

    /// <summary>ip-api.com：status/city/regionName/country/countryCode/lat/lon（注意是 lat/lon）。</summary>
    private static async Task<IpProbe?> ProbeIpApiAsync(CancellationToken ct)
    {
        using JsonDocument doc = await FetchJsonAsync("http://ip-api.com/json/?lang=zh-CN", ct);
        JsonElement root = doc.RootElement;
        if (!string.Equals(Str(root, "status"), "success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string city = Str(root, "city");
        string region = Str(root, "regionName");
        string country = Str(root, "country");
        string code = Str(root, "countryCode");
        double lat = Num(root, "lat");
        double lon = Num(root, "lon");
        bool hasCoords = ValidCoords(lat, lon);
        if (string.IsNullOrWhiteSpace(city) && !hasCoords)
        {
            return null;
        }

        return new IpProbe("ip-api.com", city, region, country, code, lat, lon, hasCoords, IsDomestic(country, code));
    }

    /// <summary>包裹单个探针：计时、吞掉所有异常，保证 Task.WhenAll 不抛。</summary>
    private static async Task<IpProbe?> SafeProbeAsync(string name, Func<CancellationToken, Task<IpProbe?>> probe, CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            IpProbe? result = await probe(ct);
            sw.Stop();
            if (result is not null)
            {
                AppEnvironment.Log($"IP 定位源 {name}：{result.City} / {result.Country} / 坐标{(result.HasCoordinates ? "有" : "无")}（{sw.ElapsedMilliseconds}ms）");
            }
            else
            {
                AppEnvironment.Log($"IP 定位源 {name}：无有效结果（{sw.ElapsedMilliseconds}ms）");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            AppEnvironment.Log($"IP 定位源 {name}：超时取消（{sw.ElapsedMilliseconds}ms）");
            return null;
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            AppEnvironment.Log($"IP 定位源 {name}：请求失败 {ex.Message}（{sw.ElapsedMilliseconds}ms）");
            return null;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppEnvironment.Log($"IP 定位源 {name}：失败（{sw.ElapsedMilliseconds}ms）", ex);
            return null;
        }
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
            "Taipei" => "台北",
            "Hong Kong" => "香港",
            "Macau" or "Macao" => "澳门",
            "Ningbo" => "宁波",
            "Wuxi" => "无锡",
            "Dongguan" => "东莞",
            "Foshan" => "佛山",
            "Jinan" => "济南",
            "Nanchang" => "南昌",
            "Guiyang" => "贵阳",
            "Nanning" => "南宁",
            "Lanzhou" => "兰州",
            "Taiyuan" => "太原",
            "Shijiazhuang" => "石家庄",
            "Haikou" => "海口",
            "Sanya" => "三亚",
            "Urumqi" or "Urumchi" => "乌鲁木齐",
            "Lhasa" => "拉萨",
            "Hohhot" => "呼和浩特",
            "Changchun" => "长春",
            "Zhuhai" => "珠海",
            "Zhongshan" => "中山",
            "Huizhou" => "惠州",
            "Shantou" => "汕头",
            "Zhanjiang" => "湛江",
            "Jiangmen" => "江门",
            "Zhaoqing" => "肇庆",
            "Wenzhou" => "温州",
            "Shaoxing" => "绍兴",
            "Jiaxing" => "嘉兴",
            "Taizhou" => "台州",
            "Jinhua" => "金华",
            "Quanzhou" => "泉州",
            "Yantai" => "烟台",
            "Weifang" => "潍坊",
            "Zibo" => "淄博",
            "Xuzhou" => "徐州",
            "Changzhou" => "常州",
            "Nantong" => "南通",
            "Yangzhou" => "扬州",
            "Yancheng" => "盐城",
            "Luoyang" => "洛阳",
            "Tangshan" => "唐山",
            "Baoding" => "保定",
            "Langfang" => "廊坊",
            "Qinhuangdao" => "秦皇岛",
            "Baotou" => "包头",
            "Jilin" => "吉林",
            "Anshan" => "鞍山",
            "Daqing" => "大庆",
            "Wuhu" => "芜湖",
            "Bengbu" => "蚌埠",
            "Jiujiang" => "九江",
            "Ganzhou" => "赣州",
            "Yichang" => "宜昌",
            "Xiangyang" => "襄阳",
            "Jingzhou" => "荆州",
            "Zhuzhou" => "株洲",
            "Xiangtan" => "湘潭",
            "Yueyang" => "岳阳",
            "Hengyang" => "衡阳",
            "Guilin" => "桂林",
            "Liuzhou" => "柳州",
            "Mianyang" => "绵阳",
            "Deyang" => "德阳",
            "Yibin" => "宜宾",
            "Zunyi" => "遵义",
            "Qujing" => "曲靖",
            "Dali" => "大理",
            "Lijiang" => "丽江",
            "Yanan" => "延安",
            "Baoji" => "宝鸡",
            "Xianyang" => "咸阳",
            "Tianshui" => "天水",
            "Yinchuan" => "银川",
            "Xining" => "西宁",
            _ => city.Trim()
        };
    }

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        AppSettings settings = _settings.Current.Clone();
        if (!settings.WeatherEnabled)
        {
            StatusText = "天气功能未启用，请在设置中开启";
            Updated?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!ValidCoords(settings.WeatherLatitude, settings.WeatherLongitude) || string.IsNullOrWhiteSpace(settings.WeatherCity))
        {
            StatusText = "请先重新定位，或在设置中选择城市";
            Updated?.Invoke(this, EventArgs.Empty);
            return;
        }
        _refreshCancellation?.Cancel();
        using CancellationTokenSource request = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _refreshCancellation = request;
        StatusText = "正在更新天气…";
        Updated?.Invoke(this, EventArgs.Empty);
        try
        {
            string url = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
                "&current=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                "&hourly=temperature_2m,precipitation_probability,precipitation&timezone=auto&forecast_days=3",
                settings.WeatherLatitude,
                settings.WeatherLongitude);

            using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using HttpResponseMessage response = await Client.SendAsync(message, request.Token);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_settings.Current.WeatherLatitude != settings.WeatherLatitude ||
                _settings.Current.WeatherLongitude != settings.WeatherLongitude ||
                _settings.Current.WeatherCity != settings.WeatherCity) return;
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

            TimeSpan offset = TimeSpan.FromSeconds(root.GetProperty("utc_offset_seconds").GetInt32());
            DateTimeOffset ParseTime(string value) => new DateTimeOffset(
                DateTime.SpecifyKind(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified), offset);
            List<HourlyForecast> hours = new();
            if (root.TryGetProperty("hourly", out JsonElement hourly))
            {
                JsonElement hourTimes = hourly.GetProperty("time");
                double? Value(string key, int index) => hourly.TryGetProperty(key, out JsonElement values)
                    && index < values.GetArrayLength() && values[index].TryGetDouble(out double number) ? number : null;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                for (int i = 0; i < hourTimes.GetArrayLength() && hours.Count < 24; i++)
                {
                    DateTimeOffset time = ParseTime(hourTimes[i].GetString()!);
                    if (time < now.AddHours(-1)) continue;
                    if (Value("temperature_2m", i) is not double temp) continue;
                    hours.Add(new HourlyForecast(time, temp, Value("precipitation_probability", i), Value("precipitation", i)));
                }
            }
            Current = new WeatherSnapshot(
                settings.WeatherCity,
                Math.Round(temperature, 1),
                Describe(code),
                Glyph(code),
                DateTimeOffset.Now,
                days)
            {
                Hours = hours,
                ObservedAt = ParseTime(current.GetProperty("time").GetString()!)
            };
            StatusText = "天气已更新 · Open-Meteo";

            AppEnvironment.Log("天气已更新：" + Current.City + " " + Current.Temperature + "°C " + Current.Description);
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (!ReferenceEquals(_refreshCancellation, request)) { }
        catch (Exception ex)
        {
            StatusText = Current is null ? "更新失败，请检查网络后重试" : "更新失败，当前显示上次成功数据";
            Updated?.Invoke(this, EventArgs.Empty);
            // Preserve the last successful snapshot.
            AppEnvironment.Log("刷新天气失败。", ex);
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, request)) _refreshCancellation = null;
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
