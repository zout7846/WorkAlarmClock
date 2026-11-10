using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WorkAlarmClock.Models;

/// <summary>
/// 全局热键动作枚举（对应 5 项可自定义组合键）。
/// </summary>
public enum HotkeyAction
{
    /// <summary>开始工作。</summary>
    StartWork,

    /// <summary>开始休息。</summary>
    StartRest,

    /// <summary>结束当天。</summary>
    EndDay,

    /// <summary>生成工作汇报。</summary>
    GenerateReport,

    /// <summary>显示/隐藏桌宠。</summary>
    TogglePet,

    /// <summary>区域截图。</summary>
    Screenshot,

    /// <summary>开始/停止专注。</summary>
    Focus,

    /// <summary>显示/隐藏桌宠（灵动岛）。</summary>
    ToggleVisible,

    /// <summary>唤起变量名生成器。</summary>
    VariableNameGenerator
}

/// <summary>
/// 单个热键组合：虚拟键码 + 修饰键。
/// 仅序列化字段（不可变记录），由设置面板编辑后整体替换。
/// </summary>
public readonly record struct HotkeyCombo
{
    /// <summary>虚拟键码（Win32 VK，与 <see cref="System.Windows.Forms.Keys"/> 数值一致）。</summary>
    public int VirtualKeyCode { get; init; }

    /// <summary>是否需要 Ctrl 修饰键。</summary>
    public bool Ctrl { get; init; }

    /// <summary>是否需要 Alt 修饰键。</summary>
    public bool Alt { get; init; }

    /// <summary>是否需要 Shift 修饰键。</summary>
    public bool Shift { get; init; }

    /// <summary>构造空组合（无键）。</summary>
    public HotkeyCombo()
    {
        VirtualKeyCode = 0;
        Ctrl = false;
        Alt = false;
        Shift = false;
    }

    /// <summary>构造带值的组合。</summary>
    /// <param name="virtualKeyCode">虚拟键码。</param>
    /// <param name="ctrl">Ctrl 修饰键。</param>
    /// <param name="alt">Alt 修饰键。</param>
    /// <param name="shift">Shift 修饰键。</param>
    public HotkeyCombo(int virtualKeyCode, bool ctrl, bool alt, bool shift)
    {
        VirtualKeyCode = virtualKeyCode;
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
    }

    /// <summary>生成本组合的可读文本，例如 "Ctrl+Alt+W"。</summary>
    /// <returns>组合键文本。</returns>
    public string ToDisplayString()
    {
        StringBuilder builder = new StringBuilder();
        if (Ctrl)
        {
            builder.Append("Ctrl+");
        }

        if (Alt)
        {
            builder.Append("Alt+");
        }

        if (Shift)
        {
            builder.Append("Shift+");
        }

        builder.Append(KeyToString(VirtualKeyCode));
        return builder.ToString();
    }

    /// <summary>把虚拟键码转换为友好名称（覆盖常用 A–Z / 0–9 / F1–F12）。</summary>
    /// <param name="virtualKeyCode">虚拟键码。</param>
    /// <returns>键名；未知返回形如 "VK123" 的占位。</returns>
    public static string KeyToString(int virtualKeyCode)
    {
        if (virtualKeyCode >= 0x41 && virtualKeyCode <= 0x5A)
        {
            // A-Z
            return ((char)virtualKeyCode).ToString(CultureInfo.InvariantCulture);
        }

        if (virtualKeyCode >= 0x30 && virtualKeyCode <= 0x39)
        {
            // 0-9（主键盘区）
            return ((char)virtualKeyCode).ToString(CultureInfo.InvariantCulture);
        }

        if (virtualKeyCode >= 0x70 && virtualKeyCode <= 0x7B)
        {
            // F1-F12
            return "F" + (virtualKeyCode - 0x6F).ToString(CultureInfo.InvariantCulture);
        }

        return "VK" + virtualKeyCode.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 热键设置集合：5 个固定动作的组合键，默认 Ctrl+Alt+W/R/E/G/S。
/// </summary>
public sealed class HotkeySettings
{
    /// <summary>开始工作（默认 Ctrl+Alt+W）。</summary>
    public HotkeyCombo StartWork { get; set; } = new HotkeyCombo(0x57, true, true, false);

    /// <summary>开始休息（默认 Ctrl+Alt+R）。</summary>
    public HotkeyCombo StartRest { get; set; } = new HotkeyCombo(0x52, true, true, false);

    /// <summary>结束当天（默认 Ctrl+Alt+E）。</summary>
    public HotkeyCombo EndDay { get; set; } = new HotkeyCombo(0x45, true, true, false);

    /// <summary>生成工作汇报（默认 Ctrl+Alt+G）。</summary>
    public HotkeyCombo GenerateReport { get; set; } = new HotkeyCombo(0x47, true, true, false);

    /// <summary>显示/隐藏桌宠（默认 Ctrl+Alt+S）。</summary>
    public HotkeyCombo TogglePet { get; set; } = new HotkeyCombo(0x53, true, true, false);

    /// <summary>区域截图（默认 Ctrl+Alt+A）。</summary>
    public HotkeyCombo Screenshot { get; set; } = new HotkeyCombo(0x41, true, true, false);

    /// <summary>开始/停止专注（默认 Ctrl+Alt+F）。</summary>
    public HotkeyCombo Focus { get; set; } = new HotkeyCombo(0x46, true, true, false);

    /// <summary>显示/隐藏桌宠（默认 Ctrl+Alt+H）。</summary>
    public HotkeyCombo ToggleVisible { get; set; } = new HotkeyCombo(0x48, true, true, false);

    /// <summary>唤起变量名生成器（默认 Ctrl+Alt+N）。</summary>
    public HotkeyCombo VariableNameGenerator { get; set; } = new HotkeyCombo(0x4E, true, true, false);

    /// <summary>按动作枚举取出对应组合。</summary>
    /// <param name="action">动作。</param>
    /// <returns>组合键。</returns>
    public HotkeyCombo GetByAction(HotkeyAction action) => action switch
    {
        HotkeyAction.StartWork => StartWork,
        HotkeyAction.StartRest => StartRest,
        HotkeyAction.EndDay => EndDay,
        HotkeyAction.GenerateReport => GenerateReport,
        HotkeyAction.Screenshot => Screenshot,
        HotkeyAction.Focus => Focus,
        HotkeyAction.ToggleVisible => ToggleVisible,
        HotkeyAction.VariableNameGenerator => VariableNameGenerator,
        _ => TogglePet
    };

    /// <summary>按动作枚举写入对应组合。</summary>
    /// <param name="action">动作。</param>
    /// <param name="combo">新组合。</param>
    public void SetByAction(HotkeyAction action, HotkeyCombo combo)
    {
        switch (action)
        {
            case HotkeyAction.StartWork:
                StartWork = combo;
                break;
            case HotkeyAction.StartRest:
                StartRest = combo;
                break;
            case HotkeyAction.EndDay:
                EndDay = combo;
                break;
            case HotkeyAction.GenerateReport:
                GenerateReport = combo;
                break;
            case HotkeyAction.Screenshot:
                Screenshot = combo;
                break;
            case HotkeyAction.Focus:
                Focus = combo;
                break;
            case HotkeyAction.ToggleVisible:
                ToggleVisible = combo;
                break;
            case HotkeyAction.VariableNameGenerator:
                VariableNameGenerator = combo;
                break;
            default:
                TogglePet = combo;
                break;
        }
    }

    /// <summary>遍历全部（动作, 组合）对，便于匹配与校验。</summary>
    /// <returns>枚举对集合。</returns>
    public IEnumerable<(HotkeyAction Action, HotkeyCombo Combo)> Enumerate()
    {
        yield return (HotkeyAction.StartWork, StartWork);
        yield return (HotkeyAction.StartRest, StartRest);
        yield return (HotkeyAction.EndDay, EndDay);
        yield return (HotkeyAction.GenerateReport, GenerateReport);
        yield return (HotkeyAction.TogglePet, TogglePet);
        yield return (HotkeyAction.Screenshot, Screenshot);
        yield return (HotkeyAction.Focus, Focus);
        yield return (HotkeyAction.ToggleVisible, ToggleVisible);
        yield return (HotkeyAction.VariableNameGenerator, VariableNameGenerator);
    }
}
