using System;
using System.Runtime.InteropServices;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 全局低层键盘钩子热键服务（增量 R-22）。
/// 在 UI 线程注册 SetWindowsHookEx(WH_KEYBOARD_LL, …)，回调即在 UI 线程消息泵中执行，
/// 直接访问业务命令线程安全，无需 Dispatcher.Invoke（会死锁）。命中 5 组自定义组合键时回调 IHotkeyActions。
/// 始终 CallNextHookEx，不抢占其他程序热键。
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly ISettingsService _settings;
    private IHotkeyActions? _actions;
    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hook = IntPtr.Zero;

    /// <summary>构造热键服务。</summary>
    /// <param name="settings">设置服务（读取 5 组自定义组合键）。</param>
    public HotkeyService(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public void Install(IHotkeyActions actions)
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _actions = actions ?? throw new ArgumentNullException(nameof(actions));

        // 必须在 UI 线程安装；回调强引用存为字段防止 GC 回收。
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            AppEnvironment.Log("安装全局键盘钩子失败，错误码：" + error);
            _proc = null;
        }
        else
        {
            AppEnvironment.Log("全局键盘钩子已安装。");
        }
    }

    /// <inheritdoc />
    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _proc = null;
        _actions = null;
        AppEnvironment.Log("全局键盘钩子已卸载。");
    }

    /// <summary>低层键盘钩子回调（运行于 UI 线程）。</summary>
    /// <param name="nCode">钩子码。</param>
    /// <param name="wParam">消息类型。</param>
    /// <param name="lParam">指向 KBDLLHOOKSTRUCT 的指针。</param>
    /// <returns>调用链下一钩子的结果。</returns>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0 && wParam == (IntPtr)NativeMethods.WM_KEYDOWN && _actions is not null)
        {
            try
            {
                int vkCode = Marshal.ReadInt32(lParam);
                bool ctrl = IsDown(NativeMethods.VK_LCONTROL) || IsDown(NativeMethods.VK_RCONTROL);
                bool alt = IsDown(NativeMethods.VK_LMENU) || IsDown(NativeMethods.VK_RMENU);
                bool shift = IsDown(NativeMethods.VK_LSHIFT) || IsDown(NativeMethods.VK_RSHIFT);

                Dispatch(vkCode, ctrl, alt, shift);
            }
            catch (Exception ex)
            {
                AppEnvironment.Log("热键回调处理异常。", ex);
            }
        }

        // 始终转发给下一钩子，绝不吞键。
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsDown(int vKey) => (NativeMethods.GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>按当前修饰键状态匹配 5 组组合键并触发对应动作。</summary>
    private void Dispatch(int vkCode, bool ctrl, bool alt, bool shift)
    {
        IHotkeyActions actions = _actions!;
        foreach ((HotkeyAction action, HotkeyCombo combo) in _settings.Current.Hotkeys.Enumerate())
        {
            if (combo.VirtualKeyCode == vkCode && combo.Ctrl == ctrl && combo.Alt == alt && combo.Shift == shift)
            {
                Fire(action, actions);
                return;
            }
        }
    }

    private static void Fire(HotkeyAction action, IHotkeyActions actions)
    {
        switch (action)
        {
            case HotkeyAction.StartWork:
                actions.StartWork();
                break;
            case HotkeyAction.StartRest:
                actions.StartRest();
                break;
            case HotkeyAction.EndDay:
                actions.EndDay();
                break;
            case HotkeyAction.GenerateReport:
                actions.GenerateReport();
                break;
            case HotkeyAction.TogglePet:
                actions.TogglePet();
                break;
            case HotkeyAction.Screenshot:
                actions.Screenshot();
                break;
            case HotkeyAction.Focus:
                actions.FocusToggle();
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Uninstall();
    }
}
