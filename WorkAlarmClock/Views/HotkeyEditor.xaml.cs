using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Views;

/// <summary>
/// 热键组合编辑控件（增量 R-22）：点击「录制」后按下组合键即捕获；也可手动勾选修饰键。
/// 双向绑定 <see cref="HotkeyCombo"/>，供设置页热键分组使用。
/// </summary>
public partial class HotkeyEditor : UserControl
{
    /// <summary>当前热键组合（双向绑定目标）。</summary>
    public static readonly DependencyProperty ComboProperty =
        DependencyProperty.Register(
            nameof(Combo),
            typeof(HotkeyCombo),
            typeof(HotkeyEditor),
            new FrameworkPropertyMetadata(default(HotkeyCombo), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnComboChanged));

    private bool _suppressChange;
    private bool _capturing;

    /// <summary>构造热键编辑控件并初始化界面。</summary>
    public HotkeyEditor()
    {
        InitializeComponent();
        CaptureButton.LostFocus += (_, _) =>
        {
            if (_capturing)
            {
                _capturing = false;
                UpdateControls();
            }
        };
        UpdateControls();
    }

    /// <summary>当前热键组合（双向绑定目标）。</summary>
    public HotkeyCombo Combo
    {
        get => (HotkeyCombo)GetValue(ComboProperty);
        set => SetValue(ComboProperty, value);
    }

    private static void OnComboChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyEditor)d).UpdateControls();

    /// <summary>根据当前组合刷新按钮文案与修饰键勾选状态。</summary>
    private void UpdateControls()
    {
        _suppressChange = true;
        try
        {
            HotkeyCombo combo = Combo;
            CaptureButton.Content = combo.VirtualKeyCode == 0
                ? "（点击录制）"
                : combo.ToDisplayString();
            CtrlBox.IsChecked = combo.Ctrl;
            AltBox.IsChecked = combo.Alt;
            ShiftBox.IsChecked = combo.Shift;
        }
        finally
        {
            _suppressChange = false;
        }
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        CaptureButton.Content = "请按下组合键…";
        CaptureButton.Focus();
    }

    private void OnModifierChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressChange)
        {
            return;
        }

        HotkeyCombo current = Combo;
        Combo = new HotkeyCombo(
            current.VirtualKeyCode,
            CtrlBox.IsChecked == true,
            AltBox.IsChecked == true,
            ShiftBox.IsChecked == true);
    }

    /// <summary>捕获键盘输入（录制态下忽略修饰键/导航键，取主键 + 当前修饰键组成组合）。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturing = false;
            UpdateControls();
            return;
        }

        // 忽略导航键与修饰键本身，等待"主键"。
        if (e.Key == Key.Tab || e.Key == Key.Enter || e.Key == Key.Space ||
            e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(e.Key);
        if (vk == 0)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        Combo = new HotkeyCombo(
            vk,
            (modifiers & ModifierKeys.Control) != 0,
            (modifiers & ModifierKeys.Alt) != 0,
            (modifiers & ModifierKeys.Shift) != 0);

        _capturing = false;
        UpdateControls();
    }
}
