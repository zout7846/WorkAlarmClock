using System;
using System.Globalization;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.ViewModels;

/// <summary>
/// 网络测速视图模型：驱动延迟/下载/上传测速，展示阶段与结果。
/// </summary>
public sealed class SpeedTestViewModel : ObservableObject
{
    public System.Collections.ObjectModel.ObservableCollection<double> DownloadTrend { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<double> UploadTrend { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<string> TrendLabels { get; } = new();
    private string _latencyText = "--";
    private string _downloadText = "--";
    private string _uploadText = "--";
    private string _stageText = "就绪";
    private bool _isTesting;
    private double _downloadMbps;
    private double _uploadMbps;
    private double _gaugeValue;
    private string _gaugeLabel = "下载";
    private string _nodeText = "节点：未选择";
    private System.Windows.Media.Brush _gaugeBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x7D, 0xF6));

    /// <summary>构造视图模型并订阅测速事件。</summary>
    public SpeedTestViewModel()
    {
        RunCommand = new RelayCommand(async () => await RunAsync());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        AppServices.SpeedTest.ProgressChanged += OnProgressChanged;
        AppServices.SpeedTest.Completed += OnCompleted;
        OnProgressChanged(this, EventArgs.Empty);
    }

    /// <summary>延迟文案。</summary>
    public string LatencyText
    {
        get => _latencyText;
        private set => SetProperty(ref _latencyText, value);
    }

    /// <summary>下载文案。</summary>
    public string DownloadText
    {
        get => _downloadText;
        private set => SetProperty(ref _downloadText, value);
    }

    /// <summary>上传文案。</summary>
    public string UploadText
    {
        get => _uploadText;
        private set => SetProperty(ref _uploadText, value);
    }

    /// <summary>阶段文案。</summary>
    public string StageText
    {
        get => _stageText;
        private set => SetProperty(ref _stageText, value);
    }

    /// <summary>是否正在测速。</summary>
    public bool IsTesting
    {
        get => _isTesting;
        private set => SetProperty(ref _isTesting, value);
    }

    /// <summary>下载速率（Mbps，供仪表盘指针使用）。</summary>
    public double DownloadMbps
    {
        get => _downloadMbps;
        private set => SetProperty(ref _downloadMbps, value);
    }

    /// <summary>上传速率（Mbps，供仪表盘指针使用）。</summary>
    public double UploadMbps
    {
        get => _uploadMbps;
        private set => SetProperty(ref _uploadMbps, value);
    }

    /// <summary>仪表盘当前显示值（下载阶段=下载速率，上传阶段=上传速率）。</summary>
    public double GaugeValue
    {
        get => _gaugeValue;
        private set => SetProperty(ref _gaugeValue, value);
    }

    /// <summary>仪表盘当前指标名（下载 / 上传）。</summary>
    public string GaugeLabel
    {
        get => _gaugeLabel;
        private set => SetProperty(ref _gaugeLabel, value);
    }

    /// <summary>仪表盘指针颜色（下载蓝 / 上传绿）。</summary>
    public System.Windows.Media.Brush GaugeBrush
    {
        get => _gaugeBrush;
        private set => SetProperty(ref _gaugeBrush, value);
    }

    /// <summary>当前测速节点文案。</summary>
    public string NodeText
    {
        get => _nodeText;
        private set => SetProperty(ref _nodeText, value);
    }

    /// <summary>开始测速命令。</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>关闭命令。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>退出订阅（窗口关闭时调用）。</summary>
    public void Detach()
    {
        AppServices.SpeedTest.ProgressChanged -= OnProgressChanged;
        AppServices.SpeedTest.Completed -= OnCompleted;
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        if (AppServices.SpeedTest.IsTesting)
        {
            return;
        }

        LatencyText = "--";
        DownloadText = "--";
        UploadText = "--";
        DownloadTrend.Clear(); UploadTrend.Clear(); TrendLabels.Clear();
        await AppServices.SpeedTest.RunAsync();
    }

    private void OnProgressChanged(object? sender, EventArgs e) => Refresh();

    private void OnCompleted(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        ISpeedTestService speed = AppServices.SpeedTest;
        IsTesting = speed.IsTesting;
        StageText = speed.StageText;

        LatencyText = "--";
        DownloadText = "--";
        UploadText = "--";
        if (speed.LatencyMs > 0)
        {
            LatencyText = speed.LatencyMs.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        if (speed.DownloadMbps > 0)
        {
            DownloadText = speed.DownloadMbps.ToString("0.0", CultureInfo.InvariantCulture) + " Mbps";
        }

        DownloadMbps = speed.DownloadMbps;
        UploadMbps = speed.UploadMbps;
        if (speed.Phase is SpeedPhase.Download or SpeedPhase.Upload)
        {
            DownloadTrend.Add(Math.Max(0, speed.DownloadMbps));
            UploadTrend.Add(Math.Max(0, speed.UploadMbps));
            TrendLabels.Add(DateTime.Now.ToString("HH:mm:ss"));
            while (DownloadTrend.Count > 90) { DownloadTrend.RemoveAt(0); UploadTrend.RemoveAt(0); TrendLabels.RemoveAt(0); }
        }
        NodeText = "节点：" + speed.NodeName;

        // 单指针随阶段切换：上传阶段显示上传速率（绿），其余显示下载速率（蓝）
        bool uploading = speed.Phase == SpeedPhase.Upload ||
            (speed.Phase is SpeedPhase.Done or SpeedPhase.Failed && GaugeLabel == "上传");
        GaugeLabel = uploading ? "上传" : "下载";
        GaugeBrush = uploading
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x32, 0xC4, 0x8D))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x7D, 0xF6));

        if (speed.Phase is SpeedPhase.Probing or SpeedPhase.Latency)
        {
            GaugeValue = 0;
        }
        else
        {
            GaugeValue = uploading ? speed.UploadMbps : speed.DownloadMbps;
        }

        if (speed.UploadMbps > 0)
        {
            UploadText = speed.UploadMbps.ToString("0.0", CultureInfo.InvariantCulture) + " Mbps";
        }
    }
}
