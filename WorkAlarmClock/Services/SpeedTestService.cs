using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using WorkAlarmClock.Infrastructure;

namespace WorkAlarmClock.Services;

/// <summary>
/// 网络测速：并发探测国内镜像取响应最快的节点测下载，Cloudflare 测上传。
/// 下载/上传过程中按时间片上报瞬时速率，仪表盘单指针随阶段切换下载/上传。
/// </summary>
public sealed class SpeedTestService : ISpeedTestService
{
    /// <summary>下载测速候选地址（并发探测，取首字节最快者）。</summary>
    private static readonly (string Url, string Name)[] DownloadCandidates =
    {
        ("https://mirrors.aliyun.com/ubuntu/ls-lR.gz", "阿里云"),
        ("https://mirrors.huaweicloud.com/ubuntu/ls-lR.gz", "华为云"),
        ("https://mirrors.cloud.tencent.com/ubuntu/ls-lR.gz", "腾讯云"),
        ("https://mirrors.tuna.tsinghua.edu.cn/ubuntu/ls-lR.gz", "清华"),
        ("https://mirrors.ustc.edu.cn/ubuntu/ls-lR.gz", "中科大"),
        ("https://speed.cloudflare.com/__down?bytes=12000000", "Cloudflare")
    };

    /// <summary>单个节点探测的最长等待时间（避免被慢节点拖住整轮测速）。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(2500);

    /// <summary>上传测速端点。</summary>
    private const string UploadUrl = "https://speed.cloudflare.com/__up";

    /// <summary>下载测试字节数（12 MB）。</summary>
    private const int DownloadBytes = 12_000_000;

    /// <summary>上传测试字节数（1 MB，避免 Cloudflare 国内慢拖时间）。</summary>
    private const int UploadBytes = 1_000_000;

    /// <summary>上传阶段最长耗时。</summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>进度上报最小间隔。</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(120);

    private static readonly HttpClient Client = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private bool _isTesting;
    private SpeedPhase _phase = SpeedPhase.Idle;
    private string _nodeName = "未选择";
    private int _latencyMs;
    private double _downloadMbps;
    private double _uploadMbps;
    private string _stageText = "就绪";

    private SynchronizationContext? _syncContext;

    /// <inheritdoc />
    public bool IsTesting => _isTesting;

    /// <inheritdoc />
    public SpeedPhase Phase => _phase;

    /// <inheritdoc />
    public string NodeName => _nodeName;

    /// <inheritdoc />
    public int LatencyMs => _latencyMs;

    /// <inheritdoc />
    public double DownloadMbps => _downloadMbps;

    /// <inheritdoc />
    public double UploadMbps => _uploadMbps;

    /// <inheritdoc />
    public string StageText => _stageText;

    /// <inheritdoc />
    public event EventHandler? ProgressChanged;

    /// <inheritdoc />
    public event EventHandler? Completed;

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_isTesting)
        {
            return;
        }

        _isTesting = true;
        _downloadMbps = 0;
        _uploadMbps = 0;
        _latencyMs = 0;
        _syncContext = SynchronizationContext.Current;

        try
        {
            SetPhase(SpeedPhase.Probing, "探测节点…");
            (string url, string name, int ttfb) = await ResolveFastestNodeAsync(cancellationToken);
            _nodeName = name;
            _latencyMs = ttfb;
            AppEnvironment.Log("测速节点：" + name + "，首字节 " + ttfb + "ms");

            // 并发探测时有竞争，选中节点后单独再测一次延迟，数值更准。
            SetPhase(SpeedPhase.Latency, "测延迟…");
            _latencyMs = await MeasureLatencyAsync(url, cancellationToken);
            AppEnvironment.Log("测速：延迟 " + _latencyMs + "ms");

            SetPhase(SpeedPhase.Download, "测下载…");
            _downloadMbps = await MeasureDownloadAsync(url, DownloadBytes, cancellationToken);
            AppEnvironment.Log("测速：下载 " + _downloadMbps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "Mbps");

            SetPhase(SpeedPhase.Upload, "测上传…");
            _uploadMbps = await MeasureUploadAsync(UploadBytes, cancellationToken);
            AppEnvironment.Log("测速：上传 " + _uploadMbps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "Mbps");

            SetPhase(SpeedPhase.Done, "完成");
            AppEnvironment.Log(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "测速完成：节点 {0} / 延迟 {1}ms / 下载 {2:0.0}Mbps / 上传 {3:0.0}Mbps",
                _nodeName, _latencyMs, _downloadMbps, _uploadMbps));
        }
        catch (OperationCanceledException)
        {
            SetPhase(SpeedPhase.Failed, "已取消");
        }
        catch (Exception ex)
        {
            if (_phase == SpeedPhase.Upload) _uploadMbps = 0;
            AppEnvironment.Log("测速失败。", ex);
            SetPhase(SpeedPhase.Failed, "失败：" + ex.Message);
        }
        finally
        {
            _isTesting = false;
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>并发探测所有候选节点，返回首字节最快的一个（同时得到延迟值）。</summary>
    private static async Task<(string url, string name, int ttfb)> ResolveFastestNodeAsync(CancellationToken ct)
    {
        Task<(string url, string name, int ttfb)>[] probes = DownloadCandidates
            .Select(candidate => ProbeAsync(candidate.Url, candidate.Name, ct))
            .ToArray();

        (string url, string name, int ttfb)[] results = await Task.WhenAll(probes);

        (string url, string name, int ttfb) best = results
            .Where(r => r.ttfb != int.MaxValue)
            .OrderBy(r => r.ttfb)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(best.url))
        {
            // 全部失败时取第一个，让后续步骤给出真实错误。
            best = (DownloadCandidates[0].Url, DownloadCandidates[0].Name + "（未连通）", 0);
        }

        return best;
    }

    /// <summary>探测单个节点：拉 1KB 计首字节耗时（单节点 2.5 秒超时，超时视为不可用）。</summary>
    private static async Task<(string url, string name, int ttfb)> ProbeAsync(string url, string name, CancellationToken ct)
    {
        using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(ProbeTimeout);

        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 1023);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

            Stopwatch stopwatch = Stopwatch.StartNew();
            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                probeCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return (url, name, int.MaxValue);
            }

            await response.Content.ReadAsByteArrayAsync(probeCts.Token);
            stopwatch.Stop();
            return (url, name, (int)Math.Max(1, stopwatch.ElapsedMilliseconds));
        }
        catch (Exception)
        {
            return (url, name, int.MaxValue);
        }
    }

    /// <summary>单独测一次延迟：对选定节点发 1 字节 Range 请求。</summary>
    private static async Task<int> MeasureLatencyAsync(string url, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            Stopwatch stopwatch = Stopwatch.StartNew();
            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync(timeout.Token);
            stopwatch.Stop();
            return (int)Math.Max(1, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("测延迟失败。", ex);
            return 0;
        }
    }

    /// <summary>测下载：Range 拉取固定字节，边读边按时间片上报瞬时速率。</summary>
    private async Task<double> MeasureDownloadAsync(string url, int bytes, CancellationToken ct)
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, bytes - 1);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        using HttpResponseMessage response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();

        long total = 0;
        long lastTotal = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan lastReport = TimeSpan.Zero;
        byte[] buffer = new byte[81920];

        await using (Stream stream = await response.Content.ReadAsStreamAsync(ct))
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;

                TimeSpan elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport >= ReportInterval)
                {
                    double instant = ToMbps(total - lastTotal, elapsed - lastReport);
                    _downloadMbps = _downloadMbps <= 0 ? instant : (_downloadMbps * 0.55) + (instant * 0.45);
                    lastTotal = total;
                    lastReport = elapsed;
                    RaiseProgress();
                }

                if (total >= bytes)
                {
                    break;
                }
            }
        }

        stopwatch.Stop();
        return ToMbps(total, stopwatch.Elapsed);
    }

    /// <summary>测上传：分块写入并上报进度；超时则保留已测得的速度。</summary>
    private async Task<double> MeasureUploadAsync(int bytes, CancellationToken ct)
    {
        byte[] payload = new byte[bytes];
        new Random(Environment.TickCount).NextBytes(payload);

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan lastReport = TimeSpan.Zero;
        long lastSent = 0;

        using ProgressContent content = new ProgressContent(payload, sent =>
        {
            TimeSpan elapsed = stopwatch.Elapsed;
            if (elapsed - lastReport < ReportInterval && sent < payload.Length)
            {
                return;
            }

            double instant = ToMbps(sent - lastSent, elapsed - lastReport);
            _uploadMbps = _uploadMbps <= 0 ? instant : (_uploadMbps * 0.55) + (instant * 0.45);
            lastSent = sent;
            lastReport = elapsed;
            RaiseProgress();
        });

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UploadTimeout);

        try
        {
            using HttpResponseMessage response = await Client.PostAsync(UploadUrl, content, timeout.Token);
            response.EnsureSuccessStatusCode();
            await response.Content.ReadAsByteArrayAsync(timeout.Token);
            stopwatch.Stop();
            return ToMbps(payload.Length, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Bytes written to a local socket buffer do not prove a successful upload.
            _uploadMbps = 0;
            throw new TimeoutException("上传测速超时，未取得有效结果；下载结果已保留。");
        }
    }

    private static double ToMbps(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0 || bytes <= 0)
        {
            return 0;
        }

        return bytes * 8.0 / elapsed.TotalSeconds / 1_000_000.0;
    }

    private void SetPhase(SpeedPhase phase, string text)
    {
        _phase = phase;
        _stageText = text;
        RaiseProgress();
    }

    /// <summary>把进度事件切回 UI 线程（上传进度来自 HttpClient 内部线程）。</summary>
    private void RaiseProgress()
    {
        SynchronizationContext? context = _syncContext;
        if (context is null || SynchronizationContext.Current == context)
        {
            ProgressChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        context.Post(_ => ProgressChanged?.Invoke(this, EventArgs.Empty), null);
    }

    /// <summary>支持上报写入进度的上传内容（分块写入，便于实时计算上传速率）。</summary>
    private sealed class ProgressContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly Action<long> _onProgress;

        public ProgressContent(byte[] data, Action<long> onProgress)
        {
            _data = data;
            _onProgress = onProgress;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            const int chunk = 65536;
            long sent = 0;

            for (int offset = 0; offset < _data.Length; offset += chunk)
            {
                int count = Math.Min(chunk, _data.Length - offset);
                await stream.WriteAsync(_data.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                sent += count;
                _onProgress(sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }
}
