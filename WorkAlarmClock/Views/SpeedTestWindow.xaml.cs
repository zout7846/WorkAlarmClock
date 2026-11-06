using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WorkAlarmClock.ViewModels;

namespace WorkAlarmClock.Views;

/// <summary>
/// 网络测速窗口：仪表盘双指针（下载蓝 / 上传绿）随测速实时摆动。
/// </summary>
public partial class SpeedTestWindow : Window
{
    /// <summary>仪表盘刻度（非线性，贴合常见测速工具读数分布）。</summary>
    private static readonly double[] Stops = { 0, 1, 5, 10, 25, 50, 100, 250, 500, 1000 };

    private const double CenterX = 160;
    private const double CenterY = 160;
    private const double Radius = 126;

    private SpeedTestViewModel? _viewModel;

    /// <summary>构造网络测速窗口。</summary>
    public SpeedTestWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildGauge();
        BindViewModel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = null;
        (DataContext as SpeedTestViewModel)?.Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => BindViewModel();

    private void BindViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as SpeedTestViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Animate(_viewModel.GaugeValue);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not null && e.PropertyName == nameof(SpeedTestViewModel.GaugeValue))
        {
            Animate(_viewModel.GaugeValue);
        }
    }

    /// <summary>绘制半圆刻度盘：弧线 + 刻度线 + 刻度值。</summary>
    private void BuildGauge()
    {
        GaugeTicks.Children.Clear();

        SolidColorBrush arcBrush = new SolidColorBrush(Color.FromRgb(0xDB, 0xE6, 0xF6));
        SolidColorBrush tickBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0xC2, 0xD6));
        SolidColorBrush labelBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xA8));

        Polyline arc = new Polyline
        {
            Stroke = arcBrush,
            StrokeThickness = 10,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        for (int i = 0; i <= 60; i++)
        {
            double angle = 180.0 - 180.0 * i / 60.0;
            arc.Points.Add(Polar(angle, Radius));
        }

        GaugeTicks.Children.Add(arc);

        for (int i = 0; i < Stops.Length; i++)
        {
            double angle = 180.0 - 180.0 * i / (Stops.Length - 1);

            Point outer = Polar(angle, Radius + 5);
            Point inner = Polar(angle, Radius - 10);
            Line tick = new Line
            {
                X1 = inner.X,
                Y1 = inner.Y,
                X2 = outer.X,
                Y2 = outer.Y,
                Stroke = tickBrush,
                StrokeThickness = i == 0 || i == Stops.Length - 1 ? 2.2 : 1.4
            };
            GaugeTicks.Children.Add(tick);

            TextBlock label = new TextBlock
            {
                Text = Stops[i].ToString("0"),
                FontSize = 9.5,
                Foreground = labelBrush
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Point pos = Polar(angle, Radius - 26);
            Canvas.SetLeft(label, pos.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, pos.Y - label.DesiredSize.Height / 2);
            GaugeTicks.Children.Add(label);
        }
    }

    /// <summary>极坐标 → 画布坐标。</summary>
    private static Point Polar(double degrees, double radius)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(CenterX + radius * Math.Cos(rad), CenterY - radius * Math.Sin(rad));
    }

    /// <summary>速率映射到指针角度（-90° 左端，+90° 右端）。</summary>
    private static double SpeedToAngle(double mbps)
    {
        if (mbps <= 0)
        {
            return -90;
        }

        for (int i = 1; i < Stops.Length; i++)
        {
            if (mbps <= Stops[i])
            {
                double segment = (mbps - Stops[i - 1]) / (Stops[i] - Stops[i - 1]);
                double index = (i - 1) + segment;
                return -90 + index / (Stops.Length - 1) * 180;
            }
        }

        return 90;
    }

    /// <summary>实时摆动指针（短动画，跟随每秒多次的进度上报）。</summary>
    private void Animate(double mbps)
    {
        DoubleAnimation animation = new DoubleAnimation(SpeedToAngle(mbps), TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        NeedleRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }
}
