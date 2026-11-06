using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WorkAlarmClock.ViewModels;

namespace WorkAlarmClock.Views;

/// <summary>
/// 强提醒全屏遮罩窗口：VirtualScreen 覆盖、置顶、禁 Alt+F4（未确认不可关闭），
/// 未确认时按设置间隔闪烁边框 + 提示音。
/// </summary>
public partial class StrongReminderWindow : Window
{
    private StrongReminderViewModel? _viewModel;

    /// <summary>构造强提醒窗口（自动铺满虚拟屏幕）。</summary>
    public StrongReminderWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // 独立非冻结画刷实例，保证颜色闪烁动画可安全附加（资源画刷可能被冻结或共享）。
        AlertBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEB, 0x57, 0x57));

        // DataContext 在构造之后由 DialogManager 注入，须监听变化再接线。
        DataContextChanged += OnDataContextChanged;
        Closed += OnWindowClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is StrongReminderViewModel oldViewModel)
        {
            oldViewModel.RepeatAlert -= OnRepeatAlert;
        }

        if (e.NewValue is StrongReminderViewModel newViewModel)
        {
            _viewModel = newViewModel;
            newViewModel.RepeatAlert += OnRepeatAlert;
        }
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.RepeatAlert -= OnRepeatAlert;
            _viewModel.Stop();
            _viewModel = null;
        }
    }

    /// <summary>未确认（AllowClose=false）时禁止任何途径关闭窗口。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel is not null && !_viewModel.AllowClose)
        {
            e.Cancel = true;
            FlashBorder();
        }
    }

    /// <summary>重复提醒：边框闪烁动画（提示音由视图模型播放）。</summary>
    private void OnRepeatAlert(object? sender, System.EventArgs e) => FlashBorder();

    /// <summary>红色警示边框闪烁三次。</summary>
    private void FlashBorder()
    {
        try
        {
            DoubleAnimation flash = new DoubleAnimation(1, 0.25, System.TimeSpan.FromMilliseconds(380))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3)
            };
            AlertBorder.BeginAnimation(OpacityProperty, flash);

            ColorAnimation colorFlash = new ColorAnimation(
                Color.FromRgb(0xEB, 0x57, 0x57),
                Color.FromRgb(0xFF, 0x8A, 0x00),
                System.TimeSpan.FromMilliseconds(380))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3)
            };
            if (AlertBorder.BorderBrush is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, colorFlash);
            }
        }
        catch (System.Exception)
        {
            // 闪烁动画失败不影响窗口可用性。
        }
    }
}
