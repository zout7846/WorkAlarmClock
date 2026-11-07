using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.ComponentModel;
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

        /// <summary>收起态灵动岛尺寸（紧凑）。</summary>
        private const double IslandWidth = 176;

        /// <summary>收起态灵动岛高度。</summary>
        private const double IslandHeight = 38;

        /// <summary>灵动岛悬停展开后的最小宽度。</summary>
        private const double IslandExpandedWidth = 288;

        /// <summary>灵动岛最大宽度（活动文案 + 悬停信息再多也不超过它）。</summary>
        private const double IslandMaxWidth = 520;

        /// <summary>灵动岛窗口相对胶囊的四周留白（同时容纳脉冲光晕与回弹过冲）。</summary>
        private const double IslandWindowPad = 12;

        /// <summary>灵动岛内容左右内边距。</summary>
        private const double IslandContentPadding = 16;

        /// <summary>状态光晕直径（圆形，需小于胶囊高度以免被轮廓裁切）。</summary>
        private const double IslandGlowDiameter = 24;

        /// <summary>状态光晕与状态文字之间的间距。</summary>
        private const double IslandGlowGap = 5;

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
        private bool _islandHovered;
        private DispatcherTimer? _noticeTimer;
        private double _restoreLeft = double.NaN;
        private double _restoreTop = double.NaN;
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
            IslandHost.SizeChanged += (_, _) => UpdateIslandShape();
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

            if (DataContext is PetViewModel petVm)
            {
                petVm.PropertyChanged += OnPetViewModelPropertyChanged;
            }
        }

        /// <summary>窗口关闭：保存位置、收缩态与亲密度。</summary>
        private void OnWindowClosed(object? sender, EventArgs e)
        {
            AppServices.Settings.SettingsChanged -= OnSettingsChanged;

            if (DataContext is PetViewModel petVm)
            {
                petVm.PropertyChanged -= OnPetViewModelPropertyChanged;
            }

            _noticeTimer?.Stop();
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

        /// <summary>鼠标进入：收起态展开灵动岛显示监控信息；展开态轻微上浮。</summary>
        private void OnWindowMouseEnter(object sender, MouseEventArgs e)
        {
            if (_collapsed)
            {
                SetIslandHover(true);
                return;
            }

            AnimateHover(1.012, -3);
        }

        /// <summary>鼠标离开：收起态收回紧凑胶囊；展开态恢复原始大小与位置。</summary>
        private void OnWindowMouseLeave(object sender, MouseEventArgs e)
        {
            if (_collapsed)
            {
                SetIslandHover(false);
                return;
            }

            AnimateHover(1.0, 0);
        }

        /// <summary>
        /// 灵动岛悬停展开/收回：只改胶囊宽度与附加信息透明度，窗口尺寸恒定。
        /// 改窗口尺寸会在屏幕顶部/边缘被系统钳回，引发 Enter/Leave 死循环。
        /// </summary>
        private void SetIslandHover(bool expanded)
        {
            if (!_collapsed || _islandHovered == expanded)
            {
                return;
            }

            _islandHovered = expanded;

            try
            {
                AnimateIslandWidth(expanded ? ComputeHoverWidth() : ComputeCompactWidth(), 220);

                IslandExtraWrap.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(expanded ? 1 : 0, TimeSpan.FromMilliseconds(expanded ? 200 : 120)));
            }
            catch (Exception ex)
            {
                Infrastructure.AppEnvironment.Log("灵动岛悬停切换失败。", ex);
            }
        }

        /// <summary>动画改变灵动岛宽度（同时重建底形与内容裁剪）。</summary>
        private void AnimateIslandWidth(double target, int milliseconds)
        {
            IslandHost.BeginAnimation(
                WidthProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        /// <summary>状态格宽度：圆形光晕 + 间距 + 状态文字实宽（文字随班次状态变化时胶囊跟着伸缩）。</summary>
        private double ComputeStateCellWidth()
        {
            string stateText = (DataContext as PetViewModel)?.StateText ?? string.Empty;
            return IslandGlowDiameter + IslandGlowGap + MeasureText(stateText, 12.5, FontWeights.SemiBold);
        }

        /// <summary>紧凑态宽度：状态光晕 + 状态文字 + 时间 +（有活动时）活动文案。</summary>
        private double ComputeCompactWidth()
        {
            double width = IslandContentPadding * 2
                + ComputeStateCellWidth()
                + 10
                + MeasureText(AppServices.Ticker.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), 13.5, FontWeights.SemiBold);

            if (DataContext is PetViewModel vm && vm.IslandActivityVisible)
            {
                width += 10 + MeasureText(vm.IslandActivityText, 11.5, FontWeights.Normal);
            }

            return Math.Clamp(width, IslandWidth, IslandMaxWidth);
        }

        /// <summary>悬停态宽度：在紧凑态基础上追加右侧监控信息。</summary>
        private double ComputeHoverWidth()
        {
            double extra = IslandExtraWrap.ActualWidth > 0 ? IslandExtraWrap.ActualWidth : 265;
            return Math.Clamp(ComputeCompactWidth() + extra, IslandExpandedWidth, IslandMaxWidth);
        }

        /// <summary>按当前字体精确量算文本宽度（用于计算胶囊目标宽度）。</summary>
        private double MeasureText(string? text, double fontSize, FontWeight weight)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal);
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                System.Windows.Media.Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }

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
                if (_collapsed)
                {
                    // 收起态单击灵动岛：恢复完整桌宠。
                    Expand();
                    SavePlacement();
                }
                else
                {
                    PlayInteraction();
                }
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

            // 距离按「窗口边缘到工作区边缘」计算，越过边界一律视为 0：
            // 否则拖到屏幕外时 ABS 距离会变大，导致贴边不吸附（左右越界、上下贴边不收起）。
            double distanceLeft = Math.Max(0, Left - area.Left);
            double distanceRight = Math.Max(0, area.Right - right);
            double distanceTop = Math.Max(0, Top - area.Top);
            double distanceBottom = Math.Max(0, area.Bottom - bottom);

            double nearest = Math.Min(distanceLeft, Math.Min(distanceRight, Math.Min(distanceTop, distanceBottom)));
            if (nearest > SnapDistance)
            {
                return;
            }

            // 灵动岛固定屏幕顶部居中，收起后无需再按吸附边贴齐。
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

        /// <summary>收缩为灵动岛：固定到当前屏幕顶部居中。</summary>
        private void Collapse()
        {
            if (_collapsed)
            {
                return;
            }

            // 记住展开态位置，之后展开回到原处。
            if (!double.IsNaN(Left) && !double.IsNaN(Top))
            {
                _restoreLeft = Left;
                _restoreTop = Top;
            }

            _collapsed = true;
            PetBody.Visibility = Visibility.Collapsed;
            IslandHost.Visibility = Visibility.Visible;
            SizeToContent = SizeToContent.Manual;

            double scale = Math.Clamp(AppServices.Settings.Current.PetScale, 0.7, 1.6);
            IslandHost.Width = ComputeCompactWidth();
            IslandHost.Height = IslandHeight;
            IslandHost.HorizontalAlignment = HorizontalAlignment.Center;

            // 窗口尺寸恒定 = 最大胶囊宽度（悬停/通知只改胶囊宽度，绝不改窗口尺寸，
            // 否则在屏幕边缘会被系统钳回 → Enter/Leave 死循环 → 栈溢出）。
            Width = (IslandMaxWidth + (IslandWindowPad * 2)) * scale;
            Height = (IslandHeight + (IslandWindowPad * 2)) * scale;

            // 固定屏幕顶部居中。
            Rect area = GetCurrentMonitorWorkArea();
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + 2 * scale;

            Root.Background = System.Windows.Media.Brushes.Transparent;

            IslandHost.Opacity = 0;
            IslandHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            IslandScale.ScaleX = 0.92;
            IslandScale.ScaleY = 0.92;
            IslandScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
            IslandScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });

            IslandExtraWrap.Opacity = 0;

            // 状态文字背后的光晕脉冲（仅透明度/缩放，安全；禁用 Effect）
            StartIslandGlowPulse();

            IslandMenuItem.Header = "展开桌宠";
        }

        /// <summary>状态光晕的脉冲：只改透明度与缩放（圆点外扩感），避免 Effect 在该透明置顶窗口上崩溃。</summary>
        private void StartIslandGlowPulse()
        {
            DoubleAnimation glowScale = new DoubleAnimation(0.86, 1.16, TimeSpan.FromMilliseconds(1100))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            IslandGlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, glowScale);
            IslandGlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, glowScale);
            IslandGlow.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.5, 0.95, TimeSpan.FromMilliseconds(1100))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        /// <summary>展开为完整桌宠（回到收起前的位置）。</summary>
        private void Expand()
        {
            if (!_collapsed)
            {
                return;
            }

            _collapsed = false;
            IslandGlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            IslandGlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            IslandGlowScale.ScaleX = 1;
            IslandGlowScale.ScaleY = 1;
            IslandGlow.BeginAnimation(OpacityProperty, null);
            IslandGlow.Opacity = 0.8;
            IslandHost.BeginAnimation(OpacityProperty, null);
            IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            IslandHost.BeginAnimation(WidthProperty, null);
            Root.Background = System.Windows.Media.Brushes.Transparent;
            Width = double.NaN;
            Height = double.NaN;
            SizeToContent = SizeToContent.WidthAndHeight;
            IslandHost.Visibility = Visibility.Collapsed;
            IslandHost.Opacity = 0;
            IslandExtraWrap.Opacity = 0;
            PetBody.Visibility = Visibility.Visible;
            AnimateOpacity(PetBody, 1.0);

            if (!double.IsNaN(_restoreLeft) && !double.IsNaN(_restoreTop))
            {
                Left = _restoreLeft;
                Top = _restoreTop;
                ClampIntoWorkArea(GetCurrentMonitorWorkArea());
            }

            IslandMenuItem.Header = "收起为灵动岛";
        }

        /// <summary>热键 / 托盘：在完整桌宠与灵动岛之间切换。</summary>
        public void ToggleCompactMode()
        {
            if (_collapsed)
            {
                Expand();
            }
            else
            {
                Collapse();
            }

            SavePlacement();
        }

        /// <summary>按当前尺寸生成胶囊底形（上下直边 + 左右半圆端头），并把内容裁剪在轮廓内。</summary>
        private void UpdateIslandShape()
        {
            double w = IslandHost.ActualWidth;
            double h = IslandHost.ActualHeight;
            if (w < 4 || h < 4)
            {
                return;
            }

            // 形状可临时用环境变量覆盖（仅用于本地视觉比对：数字=超椭圆指数，其余=胶囊）。
            string? shapeOverride = Environment.GetEnvironmentVariable("WORKALARM_TEST_SHAPE");
            Geometry geometry;
            if (double.TryParse(shapeOverride, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double overrideExponent)
                && overrideExponent > 1.5 && overrideExponent < 12)
            {
                geometry = BuildSquircle(w, h, overrideExponent);
            }
            else
            {
                geometry = BuildCapsule(w, h);
            }

            IslandShape.Data = geometry;

            // 内容不超过胶囊：把胶囊轮廓换算到内容坐标系（内容有左右内边距），
            // 越界的文字会被裁掉，而不是画到透明窗口的黑底之外。
            Geometry contentClip = geometry.Clone();
            contentClip.Transform = new TranslateTransform(-IslandContentPadding, 0);
            contentClip.Freeze();
            IslandContent.Clip = contentClip;
        }

        /// <summary>纯胶囊路径：上下直边 + 左右半圆端头（苹果灵动岛形态）。</summary>
        private static Geometry BuildCapsule(double width, double height)
        {
            double radius = height / 2;
            Size arcSize = new Size(radius, radius);

            PathFigure figure = new PathFigure
            {
                StartPoint = new Point(radius, 0),
                IsClosed = true,
                IsFilled = true
            };

            figure.Segments.Add(new LineSegment(new Point(width - radius, 0), true));
            figure.Segments.Add(new ArcSegment(new Point(width, radius), arcSize, 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(width, height - radius), true));
            figure.Segments.Add(new ArcSegment(new Point(width - radius, height), arcSize, 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(radius, height), true));
            figure.Segments.Add(new ArcSegment(new Point(0, height - radius), arcSize, 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(new Point(0, radius), true));
            figure.Segments.Add(new ArcSegment(new Point(radius, 0), arcSize, 0, false, SweepDirection.Clockwise, true));

            PathGeometry geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        /// <summary>生成超椭圆路径：指数越小越接近圆角矩形，越大越接近椭圆。</summary>
        private static Geometry BuildSquircle(double width, double height, double exponent)
        {
            double a = width / 2;
            double b = height / 2;
            const int steps = 160;

            PathFigure figure = new PathFigure { IsClosed = true, IsFilled = true };
            PolyLineSegment segment = new PolyLineSegment { IsStroked = true };

            for (int i = 0; i < steps; i++)
            {
                double t = -Math.PI / 2 + (2 * Math.PI * i / steps);
                double cos = Math.Cos(t);
                double sin = Math.Sin(t);
                double x = a * Math.Sign(cos) * Math.Pow(Math.Abs(cos), 2.0 / exponent);
                double y = b * Math.Sign(sin) * Math.Pow(Math.Abs(sin), 2.0 / exponent);
                Point point = new Point(a + x, b + y);

                if (i == 0)
                {
                    figure.StartPoint = point;
                }
                else
                {
                    segment.Points.Add(point);
                }
            }

            figure.Segments.Add(segment);
            PathGeometry geometry = new PathGeometry(new[] { figure });
            geometry.Freeze();
            return geometry;
        }

        private void OnPetViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (DataContext is not PetViewModel vm)
            {
                return;
            }

            if (e.PropertyName == nameof(PetViewModel.BubbleVisible) && vm.BubbleVisible)
            {
                ShowIslandNotice(vm.BubbleText);
                return;
            }

            // 状态文字变长/变短（切换班次状态）时，胶囊宽度跟着伸缩。
            if (_collapsed && !_islandHovered && !IsNoticeVisible &&
                e.PropertyName == nameof(PetViewModel.StateText))
            {
                AnimateIslandWidth(ComputeCompactWidth(), 200);
            }

            // 活动文案出现/消失后，胶囊宽度随之伸缩（未悬停、未弹通知时）。
            if (_collapsed && !_islandHovered && !IsNoticeVisible &&
                (e.PropertyName == nameof(PetViewModel.IslandActivityVisible) ||
                 e.PropertyName == nameof(PetViewModel.IslandActivityText)))
            {
                AnimateIslandWidth(ComputeCompactWidth(), 240);
            }
        }

        /// <summary>通知是否正在显示。</summary>
        private bool IsNoticeVisible => _noticeTimer?.IsEnabled == true;

        /// <summary>收起态弹出通知：胶囊临时加宽显示消息，5.5 秒后收回。</summary>
        private void ShowIslandNotice(string text)
        {
            if (!_collapsed || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            IslandNotifyText.Text = text;
            IslandNotifyText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            IslandExtraWrap.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(80)));

            // 通知期间让出状态/活动文案的位置，避免内容溢出胶囊后被轮廓裁掉。
            IslandStateCell.Visibility = Visibility.Collapsed;
            IslandActivityCell.Visibility = Visibility.Collapsed;

            AnimateIslandWidth(ComputeNoticeWidth(text), 240);

            if (_noticeTimer is null)
            {
                _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5.5) };
                _noticeTimer.Tick += (_, _) =>
                {
                    _noticeTimer!.Stop();
                    HideIslandNotice();
                };
            }

            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        /// <summary>按通知文本量算胶囊宽度（时间 + 通知文本，含内边距）。</summary>
        private double ComputeNoticeWidth(string text)
        {
            double timeWidth = MeasureText(
                AppServices.Ticker.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                13.5,
                FontWeights.SemiBold);
            double textWidth = Math.Min(MeasureText(text, 11.5, FontWeights.Normal), 190);
            return Math.Clamp(IslandContentPadding * 2 + timeWidth + 9 + textWidth, 200, IslandMaxWidth);
        }

        /// <summary>收回通知，宽度回到紧凑或悬停态。</summary>
        private void HideIslandNotice()
        {
            IslandNotifyText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
            IslandStateCell.Visibility = Visibility.Visible;
            IslandActivityCell.Visibility = Visibility.Visible;
            AnimateIslandWidth(_islandHovered ? ComputeHoverWidth() : ComputeCompactWidth(), 220);
        }

        /// <summary>右键菜单：隐藏桌宠（热键可再唤出）。</summary>
        private void OnHidePetClick(object sender, RoutedEventArgs e) => Hide();

        /// <summary>右键菜单：收起为灵动岛 / 展开桌宠。</summary>
        private void OnToggleIslandClick(object sender, RoutedEventArgs e)
        {
            if (_collapsed)
            {
                Expand();
            }
            else
            {
                Collapse();
            }

            SavePlacement();
        }

        /// <summary>窗口是否位于当前显示器右半边（决定灵动岛往哪侧展开）。</summary>
        private bool IsOnRightHalf()
        {
            Rect area = GetCurrentMonitorWorkArea();
            return Left > (area.Left + area.Right) / 2;
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
            int guard = 0;
            while (current is not null && guard++ < 256)
            {
                if (current is ButtonBase || current is MenuItem)
                {
                    return true;
                }

                current = GetParentSafe(current);
            }

            return false;
        }

        /// <summary>
        /// 安全地取父级：命中文本时事件源可能是 <see cref="System.Windows.Documents.Run"/> 等 ContentElement，
        /// 直接调用 <see cref="VisualTreeHelper.GetParent"/> 会抛"不是 Visual 或 Visual3D"。
        /// </summary>
        private static DependencyObject? GetParentSafe(DependencyObject node)
        {
            if (node is Visual)
            {
                return VisualTreeHelper.GetParent(node);
            }

            if (node is ContentElement content)
            {
                return ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent;
            }

            return LogicalTreeHelper.GetParent(node);
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

            // 测试钩子：强制以灵动岛形态启动（与用户实例并存调试用）。
            bool forceCollapsed = Environment.GetEnvironmentVariable("WORKALARM_TEST_COLLAPSED") == "1";

            // 先确定收起/展开布局，再定位并夹紧，避免用展开态尺寸去夹紧收起态窗口。
            if (settings.PetCollapsed || forceCollapsed)
            {
                _collapsed = true;
                PetBody.Visibility = Visibility.Collapsed;
                IslandHost.Visibility = Visibility.Visible;
                IslandHost.Opacity = 1;
                IslandHost.BeginAnimation(WidthProperty, null);
                IslandHost.Width = ComputeCompactWidth();
                IslandHost.Height = IslandHeight;
                IslandHost.HorizontalAlignment = HorizontalAlignment.Center;
                IslandExtraWrap.Opacity = 0;
                SizeToContent = SizeToContent.Manual;

                double islandScale = Math.Clamp(settings.PetScale, 0.7, 1.6);
                Width = (IslandMaxWidth + (IslandWindowPad * 2)) * islandScale;
                Height = (IslandHeight + (IslandWindowPad * 2)) * islandScale;
                Root.Background = System.Windows.Media.Brushes.Transparent;
                IslandMenuItem.Header = "展开桌宠";

                // 固定屏幕顶部居中。
                Rect islandArea = GetCurrentMonitorWorkArea();
                Left = islandArea.Left + Math.Max(0, (islandArea.Width - Width) / 2);
                Top = islandArea.Top + 2 * islandScale;

                // 收起态启动也要有光晕脉冲。
                StartIslandGlowPulse();

                // 测试钩子：启动后立刻弹一条灵动岛通知，用于验证宽度/裁切。
                if (Environment.GetEnvironmentVariable("WORKALARM_TEST_NOTICE") is { Length: > 0 } noticeText)
                {
                    Dispatcher.BeginInvoke(new Action(() => ShowIslandNotice(noticeText)));
                }

                // 测试钩子：强制模拟悬停展开（沙箱内无法移动真实光标）。
                if (Environment.GetEnvironmentVariable("WORKALARM_TEST_HOVER") == "1")
                {
                    Dispatcher.BeginInvoke(new Action(() => SetIslandHover(true)));
                }

                return;
            }

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

            // 自动尺寸在首次布局后才有 ActualWidth/Height，这里按实际尺寸再夹紧一次。
            Dispatcher.BeginInvoke(new Action(() => ClampIntoWorkArea(workArea)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>限制窗口完整可见于工作区内（优先用显式尺寸，避免布局前的 0 值）。</summary>
        private void ClampIntoWorkArea(Rect workArea)
        {
            double width = !double.IsNaN(Width) && Width > 0
                ? Width
                : (ActualWidth > 0 ? ActualWidth : ExpandedWidth);
            double height = !double.IsNaN(Height) && Height > 0
                ? Height
                : (ActualHeight > 0 ? ActualHeight : ExpandedHeight);

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
