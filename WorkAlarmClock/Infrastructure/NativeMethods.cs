using System;
using System.Runtime.InteropServices;

namespace WorkAlarmClock.Infrastructure;

/// <summary>
/// Windows 原生方法声明（P/Invoke），全部零 NuGet。
/// </summary>
internal static class NativeMethods
{
    /// <summary>MessageBeep：MB_ICONWARNING（强提醒）。</summary>
    internal const uint MB_ICONWARNING = 0x00000030U;

    /// <summary>MessageBeep：MB_ICONASTERISK（弱提醒）。</summary>
    internal const uint MB_ICONASTERISK = 0x00000040U;

    /// <summary>MessageBeep：MB_OK（确认音）。</summary>
    internal const uint MB_OK = 0x00000000U;

    /// <summary>GetLastInputInfo 使用的输入信息结构体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        /// <summary>结构体大小（字节）。</summary>
        public uint cbSize;

        /// <summary>最后一次输入时的 tick 计数（32 位）。</summary>
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>播放系统提示音（无音频资源文件）。</summary>
    /// <param name="uType">提示音类型。</param>
    /// <returns>是否播放成功。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MessageBeep(uint uType);

    /// <summary>销毁图标句柄（防止 GDI 句柄泄漏）。</summary>
    /// <param name="hIcon">图标句柄。</param>
    /// <returns>是否销毁成功。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>删除 GDI 对象（截图转换 BitmapSource 后释放 HBITMAP，防止句柄泄漏）。</summary>
    /// <param name="hObject">GDI 对象句柄。</param>
    /// <returns>是否删除成功。</returns>
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    // ===== 窗口枚举（窗口截图用） =====

    /// <summary>窗口矩形（屏幕物理像素）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        /// <summary>左边界。</summary>
        public int Left;

        /// <summary>上边界。</summary>
        public int Top;

        /// <summary>右边界。</summary>
        public int Right;

        /// <summary>下边界。</summary>
        public int Bottom;
    }

    /// <summary>EnumWindows 回调。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="lParam">用户参数。</param>
    /// <returns>true 继续枚举。</returns>
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ===== 全局低层键盘钩子（增量 R-22） =====

    /// <summary>低层键盘钩子类型。</summary>
    internal const int WH_KEYBOARD_LL = 13;

    /// <summary>键盘按下消息。</summary>
    internal const int WM_KEYDOWN = 0x0100;

    /// <summary>左 Ctrl 虚拟键码。</summary>
    internal const int VK_LCONTROL = 0xA2;

    /// <summary>右 Ctrl 虚拟键码。</summary>
    internal const int VK_RCONTROL = 0xA3;

    /// <summary>左 Alt 虚拟键码。</summary>
    internal const int VK_LMENU = 0xA4;

    /// <summary>右 Alt 虚拟键码。</summary>
    internal const int VK_RMENU = 0xA5;

    /// <summary>左 Shift 虚拟键码。</summary>
    internal const int VK_LSHIFT = 0xA0;

    /// <summary>右 Shift 虚拟键码。</summary>
    internal const int VK_RSHIFT = 0xA1;

    /// <summary>低层键盘钩子回调参数结构体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        /// <summary>虚拟键码。</summary>
        public uint vkCode;

        /// <summary>扫描码。</summary>
        public uint scanCode;

        /// <summary>标志位。</summary>
        public uint flags;

        /// <summary>时间戳。</summary>
        public uint time;

        /// <summary>附加信息。</summary>
        public IntPtr dwExtraInfo;
    }

    /// <summary>低层键盘钩子回调委托（必须保持强引用，防止 GC 回收导致崩溃）。</summary>
    /// <param name="nCode">钩子码。</param>
    /// <param name="wParam">消息类型。</param>
    /// <param name="lParam">指向 <see cref="KBDLLHOOKSTRUCT"/> 的指针。</param>
    /// <returns>调用链下一钩子的结果。</returns>
    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ===== 窗口扩展样式（桌宠不进 Alt+Tab / 任务栏，显隐不打扰当前窗口） =====

    /// <summary>扩展样式索引。</summary>
    internal const int GWL_EXSTYLE = -20;

    /// <summary>工具窗口样式：不出现在 Alt+Tab 与任务栏。</summary>
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>读取窗口样式（自动适配 32/64 位）。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="nIndex">样式索引。</param>
    /// <returns>样式值。</returns>
    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    /// <summary>写入窗口样式（自动适配 32/64 位）。</summary>
    /// <param name="hWnd">窗口句柄。</param>
    /// <param name="nIndex">样式索引。</param>
    /// <param name="dwNewLong">新样式值。</param>
    /// <returns>原样式值。</returns>
    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    /// <summary>
    /// 获取用户最后一次键鼠输入距今的时长。
    /// </summary>
    /// <returns>空闲时长；调用失败返回 <see cref="TimeSpan.Zero"/>。</returns>
    internal static TimeSpan GetIdleTime()
    {
        LASTINPUTINFO info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // dwTime 为 32 位 GetTickCount 系，必须用 unchecked uint 减法避免 49 天后溢出成负数。
        uint idleMilliseconds = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }
}
