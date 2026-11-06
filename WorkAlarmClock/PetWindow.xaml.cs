using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;
using WorkAlarmClock.ViewModels;

namespace WorkAlarmClock
{
    /// <summary>
    /// 桌面悬浮桌宠窗口：透明置顶、手工拖动、贴边吸附收缩、双击打开主面板、右键菜单。
    /// code-behind 只处理窗口几何与原始输入事件，业务语义全部在 PetViewModel 中。
    /// </summary>
    public partial class PetWindow : Window
    {
        /// <summary>吸附判定距离（像素）。</summary>
        private const double SnapDistance = 48;

        /// <summary>收缩圆形斑点直径。</summary>
        private const double DotSize = 30;

        /// <summary>展开态窗口尺寸。</summary>
        private const double ExpandedWidth = 220;

        /// <summary>展开态窗口高度。</summary>
        private const double ExpandedHeight = 420;

        /// <summary>判定"双击而非拖动"的位移阈值。</summary>
        private const double DoubleClickMoveThreshold = 6;

        /// <summary>判定"单击而非拖动"的位移阈值（与双击阈值一致）。</summary>
        private const double ClickMoveThreshold = 6;

        private bool _isDragging;
        private bool _collapsed;
        private double _dragStartLeft;
        private double _dragStartTop;
        private double _dragStartX;
        private double _dragStartY;
        private double _lastMoveDistance;

        /// <summary>构造桌宠窗口。</summary>
        public PetWindow()
        {
            InitializeComponent();
            DataContext = new PetViewModel();
        }

        /// <inheritdoc />
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 标记为工具窗口：不进入 Alt+Tab / 任务栏预览，显隐时不在系统里"多出一个窗口"。
            try
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                long style = Infrastructure.NativeMethods.GetWindowLongPtr(
                    handle, Infrastructure.NativeMethods.GWL_EXSTYLE).ToInt64();
                style |= Infrastructure.NativeMethods.WS_EX_TOOLWINDOW;
                Infrastructure.NativeMethods.SetWindowLongPtr(
                    handle, Infrastructure.NativeMethods.GWL_EXSTYLE, new IntPtr(style));
            }
            catch (Exception ex)
            {
                Infrastructure.AppEnvironment.Log("设置桌宠工具窗口样式失败。", ex);
            }
        }

        /// <summary>窗口加载：恢复记忆位置与收缩态。</summary>
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            ApplyScale();
            RestorePlacement();
            ApplyPetImage();
            AppServices.Settings.SettingsChanged += OnSettingsChanged;
        }

        /// <summary>窗口关闭：保存位置、收缩态与亲密度。</summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            AppServices.Settings.SettingsChanged -= OnSettingsChanged;
            SavePlacement();
            (DataContext as PetViewModel)?.PersistIntimacy();
        }

        /// <summary>设置变化：同步桌宠缩放与图片。</summary>
        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            ApplyScale();
            ApplyPetImage();
        }

        /// <summary>按设置里的自定义图片路径刷新桌宠素材。</summary>
        private void ApplyPetImage()
        {
            PetAvatar?.SetPetImage(AppServices.Settings.Current.PetImagePath);
        }

        /// <summary>按设置应用桌宠缩放（LayoutTransform，窗口尺寸随之自适应）。</summary>
        private void ApplyScale()
        {
            try
            {
                double scale = Math.Clamp(AppServices.Settings.Current.PetScale, 0.7, 1.6);
                if (Math.Abs(scale - 1.0) < 0.001)
                {
                    Root.LayoutTransform = Transform.Identity;
                    return;
                }

                Root.LayoutTransform = new ScaleTransform(scale, scale);
            }
            catch (Exception ex)
            {
                Infrastructure.AppEnvironment.Log("应用桌宠缩放失败。", ex);
            }
        }

        /// <summary>鼠标进入（收缩态下展开，并轻微上浮）。</summary>
        private void OnWindowMouseEnter(object sender, MouseEventArgs e)
        {
            if (_collapsed && !_isDragging)
            {
                Expand();
            }

            AnimateHover(1.012, -3);
        }

        /// <summary>鼠标离开：恢复原始大小与位置。</summary>
        private void OnWindowMouseLeave(object sender, MouseEventArgs e) => AnimateHover(1.0, 0);

        /// <summary>悬停微动效：轻微放大 + 上浮，鼠标离开复原。</summary>
        private void AnimateHover(double scale, double lift)
        {
            try
            {
                QuadraticEase ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                TimeSpan duration = TimeSpan.FromMilliseconds(160);

                HoverScale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(scale, duration) { EasingFunction = ease });
                HoverScale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(scale, duration) { EasingFunction = ease });
                HoverLift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(lift, duration) { EasingFunction = ease });
            }
            catch (Exception ex)
            {
                Infrastructure.AppEnvironment.Log("播放桌宠悬停动画失败。", ex);
            }
        }

        /// <summary>双击打开主面板。</summary>
        private void OnWindowMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_lastMoveDistance > DoubleClickMoveThreshold)
            {
                return;
            }

            e.Handled = true;
            AppServices.Notification.ShowMainPanel();
        }

        /// <summary>左键按下：开始拖动（按钮与菜单区域除外）。</summary>
        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject))
            {
                return;
            }

            Point devicePoint = PointToScreen(e.GetPosition(this));
            Point wpfPoint = DeviceToWpf(devicePoint);
            _dragStartX = wpfPoint.X;
            _dragStartY = wpfPoint.Y;
            _dragStartLeft = Left;
            _dragStartTop = Top;
            _lastMoveDistance = 0;
            _isDragging = true;
            CaptureMouse();
        }

        /// <summary>鼠标移动：拖动窗口。</summary>
        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point wpfPoint = DeviceToWpf(PointToScreen(e.GetPosition(this)));
            double deltaX = wpfPoint.X - _dragStartX;
            double deltaY = wpfPoint.Y - _dragStartY;
            _lastMoveDistance = Math.Abs(deltaX) + Math.Abs(deltaY);

            Left = _dragStartLeft + deltaX;
            Top = _dragStartTop + deltaY;
        }

        /// <summary>左键释放：结束拖动并按需吸附收缩。</summary>
        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            // 位移极小视为单击（触发互动），否则视为拖动结束（吸附/收缩/保存）。
            bool wasClick = _lastMoveDistance <= ClickMoveThreshold;
            _isDragging = false;
            ReleaseMouseCapture();

            if (wasClick)
            {
                PlayInteraction();
            }
            else
            {
                TrySnapAndCollapse();
                SavePlacement();
            }
        }

        /// <summary>单击桌宠：播放跳跃/眨眼互动动画并弹出互动台词（亲密度随点击累积）。</summary>
        private void PlayInteraction()
        {
            PetAvatar?.PlayInteraction();

            string message = DataContext is PetViewModel viewModel
                ? viewModel.RegisterInteraction()
                : "嗨~";
            AppServices.Notification.ShowBubble(message, BubbleKind.Info);

            // 亲密度提升到新等级时立刻落盘，避免异常退出丢失。
            if (message.StartsWith("升级啦", StringComparison.Ordinal))
            {
                (DataContext as PetViewModel)?.PersistIntimacy();
            }
        }

        /// <summary>靠近当前显示器工作区边缘时吸附并收缩为圆形斑点。</summary>
        private void TrySnapAndCollapse()
        {
            if (!AppServices.Settings.Current.SnapToEdges)
            {
                return;
            }

            Rect area = GetCurrentMonitorWorkArea();
            double right = Left + ActualWidth;
            double bottom = Top + ActualHeight;

            double distanceLeft = Math.Abs(Left - area.Left);
            double distanceRight = Math.Abs(area.Right - right);
            double distanceTop = Math.Abs(Top - area.Top);
            double distanceBottom = Math.Abs(area.Bottom - bottom);

            double nearest = Math.Min(distanceLeft, Math.Min(distanceRight, Math.Min(distanceTop, distanceBottom)));
            if (nearest > SnapDistance)
            {
                return;
            }

            if (distanceTop == nearest)
            {
                Top = area.Top;
            }
            else if (distanceBottom == nearest)
            {
                Top = area.Bottom - ActualHeight;
            }
            else if (distanceLeft == nearest)
            {
                Left = area.Left;
            }
            else
            {
                Left = area.Right - ActualWidth;
            }

            Collapse();
        }

        /// <summary>取桌宠当前所在显示器的「工作区」（多显示器下不再只用主屏）。</summary>
        private Rect GetCurrentMonitorWorkArea()
        {
            try
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                    Matrix? fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
                    if (fromDevice.HasValue)
                    {
                        Point topLeft = fromDevice.Value.Transform(new Point(area.Left, area.Top));
                        Point bottomRight = fromDevice.Value.Transform(new Point(area.Right, area.Bottom));
                        return new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
                    }
                }
            }
            catch (Exception ex)
            {
                Infrastructure.AppEnvironment.Log("获取显示器工作区失败，回退到主屏。", ex);
            }

            return SystemParameters.WorkArea;
        }

        /// <summary>收缩为圆形斑点（带缩放/淡入动效）。</summary>
        private void Collapse()
        {
            if (_collapsed)
            {
                return;
            }

            _collapsed = true;
            PetBody.Visibility = Visibility.Collapsed;
            CollapseDot.Visibility = Visibility.Visible;
            SizeToContent = SizeToContent.Manual;
            double scale = Math.Clamp(AppServices.Settings.Current.PetScale, 0.7, 1.6);
            Width = DotSize * scale;
            Height = DotSize * scale;

            // 斑点出现动效：从 0.4 弹到 1 + 淡入。
            CollapseDot.Opacity = 0;
            CollapseDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            DotScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
            DotScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut } });
        }

        /// <summary>展开为完整桌宠。</summary>
        private void Expand()
        {
            if (!_collapsed)
            {
                return;
            }

            _collapsed = false;
            Width = double.NaN;
            Height = double.NaN;
            SizeToContent = SizeToContent.WidthAndHeight;
            CollapseDot.Visibility = Visibility.Collapsed;
            PetBody.Visibility = Visibility.Visible;
            AnimateOpacity(PetBody, 1.0);
        }

        /// <summary>淡入淡出动画。</summary>
        private static void AnimateOpacity(UIElement target, double to)
        {
            DoubleAnimation animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(220));
            target.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        /// <summary>判断事件源是否位于按钮内（按钮区域不触发拖动）。</summary>
        private static bool IsWithinButton(DependencyObject? source)
        {
            DependencyObject? current = source;
            while (current is not null)
            {
                if (current is ButtonBase || current is MenuItem)
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        /// <summary>设备像素坐标转换为 WPF 逻辑单位（正确处理 DPI 缩放）。</summary>
        private Point DeviceToWpf(Point devicePoint)
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            Matrix? transform = source?.CompositionTarget?.TransformFromDevice;
            return transform.HasValue ? transform.Value.Transform(devicePoint) : devicePoint;
        }

        /// <summary>恢复记忆位置，非法值时回退到工作区右下角。</summary>
        private void RestorePlacement()
        {
            AppSettings settings = AppServices.Settings.Current;
            Rect workArea = SystemParameters.WorkArea;

            if (!double.IsNaN(settings.PetLeft) && !double.IsNaN(settings.PetTop))
            {
                Left = settings.PetLeft;
                Top = settings.PetTop;
            }
            else
            {
                Left = workArea.Right - ExpandedWidth - 24;
                Top = workArea.Bottom - ExpandedHeight - 24;
            }

            ClampIntoWorkArea(workArea);

            if (settings.PetCollapsed)
            {
                _collapsed = true;
                PetBody.Visibility = Visibility.Collapsed;
                CollapseDot.Visibility = Visibility.Visible;
                CollapseDot.Opacity = 1;
                DotScale.ScaleX = 1;
                DotScale.ScaleY = 1;
                SizeToContent = SizeToContent.Manual;
                double scale = Math.Clamp(AppServices.Settings.Current.PetScale, 0.7, 1.6);
                Width = DotSize * scale;
                Height = DotSize * scale;
            }

            // 自动尺寸在首次布局后才有 ActualWidth/Height，这里按实际尺寸再夹紧一次。
            Dispatcher.BeginInvoke(new Action(() => ClampIntoWorkArea(workArea)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>限制窗口完整可见于工作区内（按当前实际尺寸）。</summary>
        private void ClampIntoWorkArea(Rect workArea)
        {
            double width = ActualWidth > 0 ? ActualWidth : ExpandedWidth;
            double height = ActualHeight > 0 ? ActualHeight : ExpandedHeight;
            double maxLeft = Math.Max(workArea.Left, workArea.Right - width);
            double maxTop = Math.Max(workArea.Top, workArea.Bottom - height);
            Left = Math.Min(Math.Max(Left, workArea.Left), maxLeft);
            Top = Math.Min(Math.Max(Top, workArea.Top), maxTop);
        }

        /// <summary>保存位置与收缩态到设置文件。</summary>
        private void SavePlacement()
        {
            try
            {
                AppSettings draft = AppServices.Settings.Current.Clone();
                draft.PetLeft = Left;
                draft.PetTop = Top;
                draft.PetCollapsed = _collapsed;
                AppServices.Settings.TryUpdate(draft);
            }
            catch (Exception ex)
            {
                Infrastructure.AppEnvironment.Log("保存桌宠位置失败。", ex);
            }
        }
    }
}
