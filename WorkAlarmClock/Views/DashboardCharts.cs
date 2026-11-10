using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkAlarmClock.Views;

/// <summary>原生绘图，不缩放文字；空数据保留空态，集合更新后重绘。</summary>
public class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable), typeof(TrendChart), new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(nameof(SecondaryValues), typeof(IEnumerable), typeof(TrendChart), new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(IEnumerable), typeof(TrendChart), new FrameworkPropertyMetadata(null, Changed));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TrendChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IEnumerable? SecondaryValues { get => (IEnumerable?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }
    public IEnumerable? Labels { get => (IEnumerable?)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    protected static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(56, 122, 218));
    protected static readonly Brush Green = new SolidColorBrush(Color.FromRgb(41, 171, 149));
    protected static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(108, 130, 155));
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;
        if (e.OldValue is INotifyCollectionChanged oldItems) CollectionChangedEventManager.RemoveHandler(oldItems, chart.CollectionChanged);
        if (e.NewValue is INotifyCollectionChanged newItems) CollectionChangedEventManager.AddHandler(newItems, chart.CollectionChanged);
        chart.InvalidateVisual();
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    protected static double[] Numbers(IEnumerable? values) => values?.Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).Select(x => double.IsFinite(x) ? Math.Max(0, x) : 0).ToArray() ?? Array.Empty<double>();
    protected void Text(DrawingContext dc, string value, Point point, Brush? brush = null, double size = 11)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush ?? Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, point);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < 120 || ActualHeight < 90) return;
        var values = Numbers(Values); var secondary = Numbers(SecondaryValues);
        if (values.Length == 0 && secondary.Length == 0) { Text(dc, "暂无数据，完成记录后显示趋势", new Point(16, ActualHeight / 2)); return; }
        double max = double.IsFinite(Maximum) && Maximum > 0 ? Maximum : Math.Max(1, values.Concat(secondary).DefaultIfEmpty(1).Max() * 1.12);
        var area = new Rect(48, 12, Math.Max(1, ActualWidth - 68), Math.Max(1, ActualHeight - 42));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(229, 237, 248)), 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = area.Top + area.Height * i / 4;
            dc.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
            Text(dc, (max * (4 - i) / 4).ToString("0.#") + Unit, new Point(0, y - 7));
        }
        DrawSeries(dc, values, area, max, Blue, true);
        DrawSeries(dc, secondary, area, max, Green, false);
        var labels = Labels?.Cast<object>().Select(x => x?.ToString() ?? "").ToArray() ?? Array.Empty<string>();
        if (labels.Length > 0)
        {
            int count = Math.Min(5, labels.Length);
            for (int i = 0; i < count; i++)
            {
                int index = count == 1 ? 0 : (int)Math.Round((labels.Length - 1d) * i / (count - 1));
                double x = labels.Length == 1 ? area.Left : area.Left + area.Width * index / (labels.Length - 1);
                Text(dc, labels[index], new Point(Math.Min(area.Right - 32, x - 10), area.Bottom + 9));
            }
        }
    }
    private static void DrawSeries(DrawingContext dc, double[] values, Rect area, double max, Brush color, bool fill)
    {
        if (values.Length == 0) return;
        var points = values.Select((v, i) => new Point(area.Left + (values.Length == 1 ? .5 : i / (values.Length - 1d)) * area.Width, area.Bottom - Math.Clamp(v / max, 0, 1) * area.Height)).ToArray();
        if (points.Length == 1) { dc.DrawEllipse(color, null, points[0], 4, 4); return; }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            // Horizontal tangents give a smooth, bounded curve without invented extrema.
            for (int i = 1; i < points.Length; i++)
            {
                double middle = (points[i - 1].X + points[i].X) / 2;
                ctx.BezierTo(new Point(middle, points[i - 1].Y), new Point(middle, points[i].Y), points[i], true, false);
            }
        }
        geometry.Freeze();
        if (fill)
        {
            var shaded = PathGeometry.CreateFromGeometry(geometry).Clone(); var f = shaded.Figures[0]; f.IsFilled = true;
            f.Segments.Add(new LineSegment(new Point(area.Right, area.Bottom), true));
            f.Segments.Add(new LineSegment(new Point(area.Left, area.Bottom), true)); f.IsClosed = true;
            var tint = color.Clone(); tint.Opacity = .09; dc.DrawGeometry(tint, null, shaded);
        }
        dc.DrawGeometry(null, new Pen(color, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var values = Numbers(Values); if (values.Length == 0) return;
        int index = (int)Math.Round(Math.Clamp((e.GetPosition(this).X - 48) / Math.Max(1, ActualWidth - 68), 0, 1) * (values.Length - 1));
        var labels = Labels?.Cast<object>().ToArray();
        var second = Numbers(SecondaryValues);
        ToolTip = (labels is not null && index < labels.Length ? labels[index] + " · " : "") + values[index].ToString("0.##") + Unit + (index < second.Length ? " / " + second[index].ToString("0.##") + Unit : "");
    }
}

public sealed class DonutChart : TrendChart
{
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < 150 || ActualHeight < 110) return;
        double[] values = Numbers(Values); double total = values.Sum();
        var labels = Labels?.Cast<object>().Select(x => x?.ToString() ?? "").ToArray() ?? Array.Empty<string>();
        double radius = Math.Min(ActualHeight / 2 - 16, ActualWidth * .22); radius = Math.Max(15, radius);
        var center = new Point(radius + 14, ActualHeight / 2);
        Brush[] colors = { Blue, Green, new SolidColorBrush(Color.FromRgb(244, 177, 72)), Muted };
        double thickness = Math.Max(10, radius * .24);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(231, 238, 248)), thickness), center, radius - thickness / 2, radius - thickness / 2);
        double angle = -90;
        for (int i = 0; i < values.Length; i++)
        {
            double sweep = total == 0 ? 0 : values[i] / total * 360;
            if (sweep > 0)
            {
                var geometry = new StreamGeometry();
                Point At(double degrees) => new(center.X + (radius - thickness / 2) * Math.Cos(degrees * Math.PI / 180), center.Y + (radius - thickness / 2) * Math.Sin(degrees * Math.PI / 180));
                using (var ctx = geometry.Open()) { ctx.BeginFigure(At(angle), false, false); ctx.ArcTo(At(angle + Math.Min(359.999, sweep)), new Size(radius - thickness / 2, radius - thickness / 2), 0, sweep > 180, SweepDirection.Clockwise, true, false); }
                dc.DrawGeometry(null, new Pen(colors[i % colors.Length], thickness), geometry);
            }
            angle += sweep;
            double y = 22 + i * 30; double x = center.X + radius + 24;
            dc.DrawEllipse(colors[i % colors.Length], null, new Point(x, y + 7), 4, 4);
            Text(dc, (i < labels.Length ? labels[i] : "项目 " + (i + 1)) + "  " + (total == 0 ? "—" : (values[i] / total).ToString("P0")), new Point(x + 12, y));
        }
        Text(dc, total == 0 ? "暂无记录" : total.ToString("0.#") + Unit, new Point(center.X - 28, center.Y - 8), Blue, 14);
    }
}
