using System;
using SD = System.Drawing;
using WF = System.Windows.Forms;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>气泡上的可选动作按钮。</summary>
/// <param name="Label">按钮文案。</param>
/// <param name="Action">点击回调。</param>
public sealed record BubbleAction(string Label, Action Action);

/// <summary>桌宠气泡请求（由 NotificationService 发出，AppServices 转发给桌宠窗口）。</summary>
/// <param name="Message">气泡文本。</param>
/// <param name="Kind">气泡类型。</param>
/// <param name="ActionLabel">单动作按钮文案，null 表示无按钮。</param>
/// <param name="Action">单动作回调。</param>
public sealed record BubbleRequest(string Message, BubbleKind Kind, string? ActionLabel, Action? Action)
{
    /// <summary>多动作按钮集合（弱提醒等场景），为空时回退到单动作。</summary>
    public IReadOnlyList<BubbleAction> Actions { get; init; } = Array.Empty<BubbleAction>();
}

/// <summary>
/// 通知服务：托盘图标（GDI+ 运行时绘制）、右键菜单、气泡与提示音。
/// 属于 UI 边界服务，是 Services 层唯一持有 WinForms 控件的类型。
/// </summary>
public sealed class NotificationService : INotificationService
{
    /// <summary>托盘 tooltip 更新的最小间隔。</summary>
    private static readonly TimeSpan TooltipThrottle = TimeSpan.FromSeconds(5);

    private readonly ISettingsService _settings;

    private WF.NotifyIcon? _notifyIcon;
    private TrayMenuCallbacks? _callbacks;
    private SD.Icon? _trayNormal;
    private WorkState _lastState = WorkState.BeforeWork;
    private string _lastTooltip = string.Empty;
    private DateTimeOffset _lastTooltipUpdate = DateTimeOffset.MinValue;

    /// <summary>构造通知服务。</summary>
    /// <param name="settings">设置服务。</param>
    public NotificationService(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>桌宠气泡请求事件（AppServices 接线到桌宠窗口）。</summary>
    public event EventHandler<BubbleRequest>? BubbleRequested;

    /// <inheritdoc />
    public void InitializeTray(TrayMenuCallbacks callbacks)
    {
        _callbacks = callbacks;
        _trayNormal = LoadTrayIcon("/Assets/tray.ico") ?? (SD.Icon)SD.SystemIcons.Application.Clone();
        _notifyIcon = new WF.NotifyIcon
        {
            Icon = _trayNormal,
            Text = "WorkAlarmClock",
            ContextMenuStrip = BuildMenu(callbacks)
        };
        _notifyIcon.MouseDoubleClick += (_, _) => ShowMainPanel();
        _notifyIcon.BalloonTipClicked += (_, _) => ShowMainPanel();
        _notifyIcon.Visible = true;
    }

    /// <inheritdoc />
    public void ShowMainPanel()
    {
        try
        {
            _callbacks?.openMainPanel();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("打开主面板失败。", ex);
        }
    }

    /// <inheritdoc />
    public void ShowBubble(string message, BubbleKind kind, string? actionLabel = null, Action? action = null)
    {
        BubbleRequested?.Invoke(this, new BubbleRequest(message, kind, actionLabel, action));
    }

    /// <inheritdoc />
    public void ShowBubbleWithActions(string message, BubbleKind kind, IReadOnlyList<BubbleAction> actions)
    {
        BubbleRequested?.Invoke(this, new BubbleRequest(message, kind, null, null)
        {
            Actions = actions ?? Array.Empty<BubbleAction>()
        });
    }

    /// <inheritdoc />
    public void ShowTrayBalloon(string title, string message)
    {
        try
        {
            if (_notifyIcon is null)
            {
                return;
            }

            _notifyIcon.ShowBalloonTip(5000, title, message, WF.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("显示托盘通知失败。", ex);
        }
    }

    /// <inheritdoc />
    public void PlaySound(SoundKind kind)
    {
        if (!_settings.Current.SoundEnabled)
        {
            return;
        }

        uint beepType = kind switch
        {
            SoundKind.Strong => NativeMethods.MB_ICONWARNING,
            SoundKind.Weak => NativeMethods.MB_ICONASTERISK,
            _ => NativeMethods.MB_OK
        };

        try
        {
            switch (kind)
            {
                case SoundKind.Strong:
                    System.Media.SystemSounds.Exclamation.Play();
                    break;
                case SoundKind.Weak:
                    System.Media.SystemSounds.Asterisk.Play();
                    break;
                default:
                    System.Media.SystemSounds.Beep.Play();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("播放系统提示音失败，回退 MessageBeep。", ex);
            try
            {
                NativeMethods.MessageBeep(beepType);
            }
            catch (Exception innerEx)
            {
                AppEnvironment.Log("回退提示音播放失败。", innerEx);
            }
        }
    }

    /// <inheritdoc />
    public void UpdateTray(WorkState state, string tooltip)
    {
        try
        {
            bool stateChanged = state != _lastState;
            if (stateChanged)
            {
                _lastState = state;
            }

            if (_notifyIcon is null)
            {
                return;
            }

            string text = TextFormats.Truncate(tooltip, 63);
            DateTimeOffset now = DateTimeOffset.Now;
            if (!stateChanged && text == _lastTooltip)
            {
                return;
            }

            if (!stateChanged && now - _lastTooltipUpdate < TooltipThrottle)
            {
                return;
            }

            _notifyIcon.Text = text;
            _lastTooltip = text;
            _lastTooltipUpdate = now;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("更新托盘状态失败。", ex);
        }
    }

    /// <summary>释放托盘资源与图标对象（必须调用，避免幽灵图标）。</summary>
    public void Dispose()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _trayNormal?.Dispose();
        _trayNormal = null;
    }

    /// <summary>构建托盘右键菜单（七项）。</summary>
    private static WF.ContextMenuStrip BuildMenu(TrayMenuCallbacks callbacks)
    {
        WF.ContextMenuStrip menu = new WF.ContextMenuStrip();
        menu.Items.Add(CreateItem("打开主面板", callbacks.openMainPanel));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(CreateItem("开始工作", callbacks.startWork));
        menu.Items.Add(CreateItem("开始休息", callbacks.startRest));
        menu.Items.Add(CreateItem("结束当天", callbacks.endDay));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(CreateItem("生成工作汇报", callbacks.generateReport));
        menu.Items.Add(CreateItem("开始/停止专注", callbacks.focusToggle));
        menu.Items.Add(CreateItem("区域截图", callbacks.screenshot));
        menu.Items.Add(CreateItem("设置", callbacks.openSettings));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(CreateItem("显示/隐藏桌宠", callbacks.togglePet));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(CreateItem("退出", callbacks.exitApp));
        return menu;
    }

    /// <summary>创建菜单项并绑定安全回调。</summary>
    private static WF.ToolStripMenuItem CreateItem(string text, Action action)
    {
        WF.ToolStripMenuItem item = new WF.ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppEnvironment.Log("托盘菜单项执行失败：" + text, ex);
            }
        };
        return item;
    }

    /// <summary>从嵌入资源加载托盘图标（按系统小图标尺寸挑选最合适的一帧）。</summary>
    private static SD.Icon? LoadTrayIcon(string relativeUri)
    {
        try
        {
            System.Windows.Resources.StreamResourceInfo? info =
                System.Windows.Application.GetResourceStream(new Uri(relativeUri, UriKind.Relative));
            if (info?.Stream is null)
            {
                AppEnvironment.Log("托盘图标资源缺失：" + relativeUri);
                return null;
            }

            using System.IO.Stream stream = info.Stream;
            using SD.Icon loaded = new SD.Icon(stream, WF.SystemInformation.SmallIconSize);
            return (SD.Icon)loaded.Clone();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("加载托盘图标失败：" + relativeUri, ex);
            return null;
        }
    }
}
