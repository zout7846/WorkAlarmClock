using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>OS-registered global hotkeys with a dedicated message window.</summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private HwndSource? _source;
    private IHotkeyActions? _actions;
    private int _generation;
    private string? _bindings;

    public HotkeyService(ISettingsService settings) => _settings = settings;

    public void Install(IHotkeyActions actions)
    {
        if (_source is not null) return;
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _source = new HwndSource(new HwndSourceParameters("WorkAlarmClock.GlobalHotkeys")
        {
            ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0
        });
        _source.AddHook(WindowProc);
        _settings.SettingsChanged += OnSettingsChanged;
        RegisterAll();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => RegisterAll();

    private void RegisterAll()
    {
        if (_source is null) return;
        string bindings = string.Join(";", _settings.Current.Hotkeys.Enumerate()
            .Select(item => $"{item.Item1}:{item.Item2.VirtualKeyCode}:{item.Item2.Ctrl}:{item.Item2.Alt}:{item.Item2.Shift}"));
        if (_bindings == bindings) return;
        _bindings = bindings;
        _generation++;
        foreach (int id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        var failures = new List<string>();
        foreach (var (action, combo) in _settings.Current.Hotkeys.Enumerate())
        {
            int id = 100 + (int)action;
            uint modifiers = 0x4000u | (combo.Alt ? 1u : 0u) | (combo.Ctrl ? 2u : 0u) | (combo.Shift ? 4u : 0u);
            if (combo.VirtualKeyCode > 0 && RegisterHotKey(_source.Handle, id, modifiers, (uint)combo.VirtualKeyCode))
            {
                _registered.Add(id, action);
                AppEnvironment.Log($"Global hotkey registered: {action} = {combo.ToDisplayString()}");
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                failures.Add(combo.ToDisplayString());
                AppEnvironment.LogWarning($"Global hotkey failed: {action} = {combo.ToDisplayString()}, Win32={error}");
            }
        }
        if (failures.Count > 0)
        {
            string keys = string.Join(", ", failures);
            PageNotifications.Publish("快捷键注册失败", keys + " 可能已被其他程序占用，请在设置 → 热键中更换。", "错误");
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            int generation = _generation;
            _source!.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (_actions is null || generation != _generation) return;
                try { Fire(action, _actions); }
                catch (Exception ex) { AppEnvironment.Log("Global hotkey action failed: " + action, ex); }
            }));
        }
        return IntPtr.Zero;
    }

    public void Uninstall()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        _generation++;
        if (_source is not null)
        {
            foreach (int id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
            _registered.Clear();
            _source.RemoveHook(WindowProc);
            _source.Dispose();
            _source = null;
        }
        _actions = null;
        _bindings = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

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
            case HotkeyAction.ToggleVisible:
                actions.ToggleVisible();
                break;
            case HotkeyAction.VariableNameGenerator:
                actions.OpenVariableNameGenerator();
                break;
        }
    }

    public void Dispose() => Uninstall();
}
