using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.Views;

/// <summary>
/// 区域截图遮罩窗：铺满整个虚拟屏幕并压暗冻结画面。
/// 支持两种方式：拖拽框选区域，或把鼠标移到某个窗口上单击截取整个窗口。
/// 选区尺寸以「截图位图像素」为单位回传。
/// </summary>
public partial class ScreenshotOverlayWindow : Window
{
    /// <summary>小于该尺寸（DIP）的拖拽视为误触。</summary>
    private const double MinSelectionSize = 6;

    /// <summary>判定为"单击窗口"的最大位移（DIP）。</summary>
    private const double ClickTolerance = 6;

    private readonly BitmapSource _frozen;
    private readonly IReadOnlyList<WindowTarget> _windows;

    private Point _startPoint;
    private bool _dragging;
    private Rect _selection;
    private WindowTarget? _hoverWindow;

    /// <summary>构造遮罩窗。</summary>
    /// <param name="frozen">冻结的全屏快照。</param>
    /// <param name="bounds">虚拟屏幕的 DIP 矩形。</param>
    /// <param name="windows">可截取的窗口列表（位图像素坐标）。</param>
    public ScreenshotOverlayWindow(BitmapSource frozen, Rect bounds, IReadOnlyList<WindowTarget>? windows = null)
    {
        InitializeComponent();

        _frozen = frozen;
        _windows = windows ?? Array.Empty<WindowTarget>();
        Frozen.Source = frozen;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        Loaded += OnLoaded;
    }

    /// <summary>完成选区（参数为位图像素矩形）。</summary>
    public event EventHandler<Int32Rect>? CaptureCompleted;

    /// <summary>用户取消。</summary>
    public event EventHandler? CaptureCancelled;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
        Keyboard.Focus(this);
        ResetShade();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _startPoint = e.GetPosition(Root);
        _dragging = true;
        _selection = Rect.Empty;
        SelectionRect.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
        HintBar.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        Point current = e.GetPosition(Root);

        if (!_dragging)
        {
            UpdateWindowHover(current);
            return;
        }

        _selection = new Rect(
            Math.Min(_startPoint.X, current.X),
            Math.Min(_startPoint.Y, current.Y),
            Math.Abs(current.X - _startPoint.X),
            Math.Abs(current.Y - _startPoint.Y));

        ApplySelection(_selection);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        bool isClick = _selection.IsEmpty
            || (_selection.Width < ClickTolerance && _selection.Height < ClickTolerance);

        // 单击某个窗口：直接截取该窗口。
        if (isClick && _hoverWindow is not null)
        {
            Int32Rect windowRect = _hoverWindow.PixelRect;
            if (windowRect.Width > 0 && windowRect.Height > 0)
            {
                CaptureCompleted?.Invoke(this, windowRect);
                Close();
                return;
            }
        }

        Rect selection = _selection;
        if (selection.IsEmpty || selection.Width < MinSelectionSize || selection.Height < MinSelectionSize)
        {
            HintBar.Visibility = Visibility.Visible;
            ResetShade();
            return;
        }

        Int32Rect pixelRect = ToPixelRect(selection);
        if (pixelRect.Width <= 0 || pixelRect.Height <= 0)
        {
            ResetShade();
            return;
        }

        CaptureCompleted?.Invoke(this, pixelRect);
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
        }
    }

    private void OnWindowRightButtonUp(object sender, MouseButtonEventArgs e) => Cancel();

    private void Cancel()
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }

        CaptureCancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>鼠标悬停时高亮光标下的窗口，并提示窗口标题。</summary>
    private void UpdateWindowHover(Point position)
    {
        Int32Rect pixel = ToPixelRect(new Rect(position.X, position.Y, 0.5, 0.5));
        WindowTarget? hit = null;

        foreach (WindowTarget target in _windows)
        {
            Int32Rect rect = target.PixelRect;
            if (pixel.X >= rect.X && pixel.X <= rect.X + rect.Width &&
                pixel.Y >= rect.Y && pixel.Y <= rect.Y + rect.Height)
            {
                hit = target;
                break;
            }
        }

        if (hit is null)
        {
            _hoverWindow = null;
            WindowHighlight.Visibility = Visibility.Collapsed;
            WindowTitle.Visibility = Visibility.Collapsed;
            return;
        }

        _hoverWindow = hit;
        Rect dip = PixelToDip(hit.PixelRect);
        SetRect(WindowHighlight, dip.X, dip.Y, dip.Width, dip.Height);
        WindowHighlight.Visibility = Visibility.Visible;

        WindowTitleText.Text = hit.Title + "  (" + hit.PixelRect.Width + " × " + hit.PixelRect.Height + ")";
        WindowTitle.Visibility = Visibility.Visible;
        Canvas.SetLeft(WindowTitle, dip.X);
        Canvas.SetTop(WindowTitle, dip.Y > 30 ? dip.Y - 28 : dip.Y + 6);
    }

    /// <summary>DIP 选区 → 位图像素矩形（按实际显示尺寸等比换算，兼容 DPI 缩放）。</summary>
    private Int32Rect ToPixelRect(Rect selection)
    {
        double scaleX = _frozen.PixelWidth / Math.Max(1, ActualWidth);
        double scaleY = _frozen.PixelHeight / Math.Max(1, ActualHeight);

        int x = (int)Math.Round(selection.X * scaleX);
        int y = (int)Math.Round(selection.Y * scaleY);
        int width = (int)Math.Round(selection.Width * scaleX);
        int height = (int)Math.Round(selection.Height * scaleY);

        x = Math.Max(0, Math.Min(x, _frozen.PixelWidth - 1));
        y = Math.Max(0, Math.Min(y, _frozen.PixelHeight - 1));
        width = Math.Min(width, _frozen.PixelWidth - x);
        height = Math.Min(height, _frozen.PixelHeight - y);

        return new Int32Rect(x, y, Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>位图像素矩形 → DIP 矩形。</summary>
    private Rect PixelToDip(Int32Rect rect)
    {
        double scaleX = Math.Max(1, ActualWidth) / Math.Max(1, _frozen.PixelWidth);
        double scaleY = Math.Max(1, ActualHeight) / Math.Max(1, _frozen.PixelHeight);
        return new Rect(rect.X * scaleX, rect.Y * scaleY, rect.Width * scaleX, rect.Height * scaleY);
    }

    /// <summary>刷新遮罩与选区边框。</summary>
    private void ApplySelection(Rect selection)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        double left = Math.Max(0, Math.Min(selection.Left, width));
        double top = Math.Max(0, Math.Min(selection.Top, height));
        double right = Math.Max(0, Math.Min(selection.Right, width));
        double bottom = Math.Max(0, Math.Min(selection.Bottom, height));

        SetRect(ShadeTop, 0, 0, width, top);
        SetRect(ShadeBottom, 0, bottom, width, Math.Max(0, height - bottom));
        SetRect(ShadeLeft, 0, top, left, Math.Max(0, bottom - top));
        SetRect(ShadeRight, right, top, Math.Max(0, width - right), Math.Max(0, bottom - top));

        SetRect(SelectionRect, left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        SelectionRect.Visibility = Visibility.Visible;

        SizeText.Text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} × {1}",
            (int)Math.Round(right - left),
            (int)Math.Round(bottom - top));

        SizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(SizeLabel, Math.Max(0, left));
        Canvas.SetTop(SizeLabel, top > 26 ? top - 26 : top + 4);
    }

    /// <summary>无选区时整屏压暗。</summary>
    private void ResetShade()
    {
        SelectionRect.Visibility = Visibility.Collapsed;
        SizeLabel.Visibility = Visibility.Collapsed;
        SetRect(ShadeTop, 0, 0, ActualWidth, ActualHeight);
        SetRect(ShadeBottom, 0, 0, 0, 0);
        SetRect(ShadeLeft, 0, 0, 0, 0);
        SetRect(ShadeRight, 0, 0, 0, 0);
    }

    private static void SetRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
