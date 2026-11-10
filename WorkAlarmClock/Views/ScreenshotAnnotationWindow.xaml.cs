using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace WorkAlarmClock.Views;

/// <summary>
/// 截图标注窗：在截图上画矩形 / 箭头 / 自由笔迹，支持撤销、复制与保存 PNG。
/// 合成时按截图原始像素渲染，标注不会因窗口缩放而失真。
/// </summary>
public partial class ScreenshotAnnotationWindow : Window
{
    private readonly BitmapSource _image;
    private readonly string _directory;
    private readonly bool _copyAfterSave;

    private string _tool = "Rect";
    private Color _color = Color.FromRgb(0xFF, 0x4D, 0x4F);
    private Point _startPoint;
    private bool _drawing;
    private Shape? _currentShape;
    private Polyline? _currentPen;

    /// <summary>构造标注窗。</summary>
    /// <param name="image">裁剪后的截图。</param>
    /// <param name="directory">保存目录。</param>
    /// <param name="copyAfterSave">保存后是否复制到剪贴板。</param>
    public ScreenshotAnnotationWindow(BitmapSource image, string directory, bool copyAfterSave)
    {
        InitializeComponent();

        _image = image;
        _directory = directory;
        _copyAfterSave = copyAfterSave;

        ImageHost.Width = image.PixelWidth;
        ImageHost.Height = image.PixelHeight;
        CanvasImage.Source = image;
        StatusText.Text = "尺寸 " + image.PixelWidth + " × " + image.PixelHeight +
                          "　·　保存到：" + directory;

        if (Environment.GetEnvironmentVariable("WORKALARM_TEST_ROI_AUTOSAVE") == "1")
        {
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                AppEnvironment.Log("测试钩子：自动保存标注截图。");
                OnSave(this, new RoutedEventArgs());
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>保存成功事件（参数为文件路径）。</summary>
    public event EventHandler<string>? Saved;

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
        {
            return;
        }

        RectTool.IsChecked = ReferenceEquals(button, RectTool);
        ArrowTool.IsChecked = ReferenceEquals(button, ArrowTool);
        PenTool.IsChecked = ReferenceEquals(button, PenTool);
        TextTool.IsChecked = ReferenceEquals(button, TextTool);
        MosaicTool.IsChecked = ReferenceEquals(button, MosaicTool);
        _tool = button.Tag as string ?? "Rect";
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string hex)
        {
            _color = (Color)ColorConverter.ConvertFromString(hex);
        }
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(ShapeLayer);

        // 文字工具：放置一个可编辑文本框。
        if (_tool == "Text")
        {
            PlaceTextBox(_startPoint);
            return;
        }

        _drawing = true;
        ShapeLayer.CaptureMouse();

        SolidColorBrush stroke = new SolidColorBrush(_color);
        stroke.Freeze();

        switch (_tool)
        {
            case "Arrow":
                ShapePath arrow = new ShapePath
                {
                    Stroke = stroke,
                    StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                _currentShape = arrow;
                ShapeLayer.Children.Add(arrow);
                break;
            case "Pen":
                Polyline pen = new Polyline
                {
                    Stroke = stroke,
                    StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                pen.Points.Add(_startPoint);
                _currentPen = pen;
                _currentShape = pen;
                ShapeLayer.Children.Add(pen);
                break;
            default:
                Rectangle rect = new Rectangle
                {
                    Stroke = stroke,
                    StrokeThickness = 3,
                    Fill = Brushes.Transparent
                };
                Canvas.SetLeft(rect, _startPoint.X);
                Canvas.SetTop(rect, _startPoint.Y);
                _currentShape = rect;
                ShapeLayer.Children.Add(rect);
                break;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _currentShape is null)
        {
            return;
        }

        Point current = e.GetPosition(ShapeLayer);

        if (_currentPen is not null)
        {
            _currentPen.Points.Add(current);
            return;
        }

        if (_currentShape is ShapePath arrow)
        {
            arrow.Data = BuildArrow(_startPoint, current);
            return;
        }

        if (_currentShape is Rectangle rect)
        {
            double x = Math.Min(_startPoint.X, current.X);
            double y = Math.Min(_startPoint.Y, current.Y);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = Math.Abs(current.X - _startPoint.X);
            rect.Height = Math.Abs(current.Y - _startPoint.Y);
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawing && _tool == "Mosaic" && _currentShape is Rectangle selection)
        {
            ShapeLayer.Children.Remove(selection);
            int x = (int)Math.Clamp(Canvas.GetLeft(selection), 0, _image.PixelWidth);
            int y = (int)Math.Clamp(Canvas.GetTop(selection), 0, _image.PixelHeight);
            int width = (int)Math.Min(selection.Width, _image.PixelWidth - x);
            int height = (int)Math.Min(selection.Height, _image.PixelHeight - y);
            if (width > 0 && height > 0)
            {
                // Flatten all existing annotations before pixelating, including text.
                var crop = new CroppedBitmap(Compose(), new Int32Rect(x, y, width, height));
                int smallWidth = Math.Max(1, width / 14);
                int smallHeight = Math.Max(1, height / 14);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) dc.DrawImage(crop, new Rect(0, 0, smallWidth, smallHeight));
                var pixels = new RenderTargetBitmap(smallWidth, smallHeight, 96, 96, PixelFormats.Pbgra32);
                pixels.Render(visual);
                pixels.Freeze();
                var mosaic = new Image { Source = pixels, Width = width, Height = height, Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(mosaic, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(mosaic, x);
                Canvas.SetTop(mosaic, y);
                ShapeLayer.Children.Add(mosaic);
                StatusText.Text = "已添加马赛克，可撤销；复制与保存将包含马赛克。";
            }
        }
        _drawing = false;
        _currentShape = null;
        _currentPen = null;
        ShapeLayer.ReleaseMouseCapture();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (ShapeLayer.Children.Count > 0)
        {
            ShapeLayer.Children.RemoveAt(ShapeLayer.Children.Count - 1);
        }
    }

    /// <summary>在指定位置放置文字标注文本框，失焦后固化为纯文本。</summary>
    private void PlaceTextBox(Point position)
    {
        SolidColorBrush foreground = new SolidColorBrush(_color);
        foreground.Freeze();

        TextBox box = new TextBox
        {
            Text = string.Empty,
            Foreground = foreground,
            CaretBrush = foreground,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            MinWidth = 40,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };

        Canvas.SetLeft(box, position.X);
        Canvas.SetTop(box, position.Y);
        ShapeLayer.Children.Add(box);

        box.LostKeyboardFocus += (_, _) =>
        {
            box.BorderThickness = new Thickness(0);
            box.Background = System.Windows.Media.Brushes.Transparent;
        };

        box.Focus();
        box.SelectAll();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            BitmapSource composed = Compose();
            bool ok = ScreenshotService.TryCopyToClipboard(composed);
            StatusText.Text = ok ? "已复制到剪贴板。" : "复制失败，请稍后重试。";
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("复制标注截图失败。", ex);
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            BitmapSource composed = Compose();
            Directory.CreateDirectory(_directory);
            string path = ScreenshotService.BuildFilePath(_directory);

            using (FileStream stream = File.Create(path))
            {
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(composed));
                encoder.Save(stream);
            }

            if (_copyAfterSave)
            {
                ScreenshotService.TryCopyToClipboard(composed);
            }

            StatusText.Text = "已保存：" + path;
            Saved?.Invoke(this, path);
            Close();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("保存标注截图失败。", ex);
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>把「图片 + 标注」按原始像素合成一张位图。</summary>
    private BitmapSource Compose()
    {
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            Rect bounds = new Rect(0, 0, _image.PixelWidth, _image.PixelHeight);
            context.DrawImage(_image, bounds);
            VisualBrush annotations = new VisualBrush(ShapeLayer)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = bounds,
                Stretch = Stretch.Fill
            };
            context.DrawRectangle(annotations, null, bounds);
        }

        RenderTargetBitmap bitmap = new RenderTargetBitmap(
            _image.PixelWidth,
            _image.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>构造箭头几何：主线 + 两条箭头翼。</summary>
    private static Geometry BuildArrow(Point from, Point to)
    {
        Vector direction = to - from;
        if (direction.Length < 1)
        {
            return Geometry.Empty;
        }

        direction.Normalize();
        Vector normal = new Vector(-direction.Y, direction.X);
        const double headLength = 14;
        const double headWidth = 6;

        Point left = to - direction * headLength + normal * headWidth;
        Point right = to - direction * headLength - normal * headWidth;

        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(from, false, false);
            context.LineTo(to, true, true);
            context.BeginFigure(left, false, false);
            context.LineTo(to, true, true);
            context.LineTo(right, true, true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
