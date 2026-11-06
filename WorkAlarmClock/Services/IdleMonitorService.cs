using System;
using Microsoft.Win32;
using WorkAlarmClock.Infrastructure;

namespace WorkAlarmClock.Services;

/// <summary>
/// 锁屏与空闲检测服务：SessionSwitch + GetLastInputInfo 抽样，只在边沿触发事件。
/// </summary>
public sealed class IdleMonitorService : IIdleMonitorService, IDisposable
{
    /// <summary>每 N 个节拍抽样一次空闲（节拍 1 秒 → 5 秒抽样）。</summary>
    private const int SampleEveryTicks = 5;

    private readonly ISettingsService _settings;
    private readonly ITicker _ticker;

    private bool _started;
    private bool _locked;
    private bool _idle;
    private int _tickCounter;
    private TimeSpan _idleSpan = TimeSpan.Zero;
    private bool _awaitingFreshInput;

    /// <summary>构造检测服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="ticker">时钟节拍。</param>
    public IdleMonitorService(ISettingsService settings, ITicker ticker)
    {
        _settings = settings;
        _ticker = ticker;
    }

    /// <inheritdoc />
    public bool IsLocked => _locked;

    /// <inheritdoc />
    public bool IsIdle => _idle;

    /// <inheritdoc />
    public TimeSpan CurrentIdleSpan => _idleSpan;

    /// <inheritdoc />
    public event EventHandler? Locked;

    /// <inheritdoc />
    public event EventHandler? Unlocked;

    /// <inheritdoc />
    public event EventHandler? IdleDetected;

    /// <inheritdoc />
    public event EventHandler? ActivityResumed;

    /// <inheritdoc />
    public void Start()
    {
        if (_started)
        {
            return;
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ticker.Tick += OnTick;
        _started = true;
        AppEnvironment.Log("空闲与锁屏检测已启动。");
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        // 必须退订静态系统事件，避免长驻托盘应用的事件泄漏。
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _ticker.Tick -= OnTick;
        _started = false;
        _idle = false;
        AppEnvironment.Log("空闲与锁屏检测已停止。");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _tickCounter++;
        if (!_started || _tickCounter % SampleEveryTicks != 0)
        {
            return;
        }

        // 锁屏期间抑制空闲检测，防止双重休息来源打架。
        if (_locked)
        {
            return;
        }

        _idleSpan = NativeMethods.GetIdleTime();
        TimeSpan threshold = TimeSpan.FromMinutes(Math.Max(1, _settings.Current.IdleThresholdMinutes));

        // 睡眠唤醒后的宽限期：GetLastInputInfo 差值包含整段睡眠时间，
        // 在用户产生第一次新的键鼠输入之前不视为空闲（T05 规则：唤醒不写虚假 Idle 流水）。
        if (_awaitingFreshInput)
        {
            if (_idleSpan < threshold)
            {
                _awaitingFreshInput = false;
            }

            return;
        }

        if (!_idle && _idleSpan >= threshold)
        {
            _idle = true;
            AppEnvironment.Log("检测到空闲，已空闲 " + TextFormats.DurationToChinese(_idleSpan));
            IdleDetected?.Invoke(this, EventArgs.Empty);
        }
        else if (_idle && _idleSpan < threshold)
        {
            _idle = false;
            AppEnvironment.Log("检测到键鼠恢复活动。");
            ActivityResumed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>睡眠唤醒：重置空闲基线并进入宽限期；时长重算与作息对账由 Ticker 下一拍自愈。</summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _idle = false;
        _awaitingFreshInput = true;
        _tickCounter = 0;
        _idleSpan = TimeSpan.Zero;
        AppEnvironment.Log("系统从睡眠中唤醒：抑制虚假空闲检测，等待新的键鼠输入。");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.RemoteDisconnect:
                _locked = true;
                _idle = false;
                _idleSpan = TimeSpan.Zero;
                AppEnvironment.Log("系统锁屏/离开：" + e.Reason);
                Locked?.Invoke(this, EventArgs.Empty);
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.RemoteConnect:
                _locked = false;
                _idle = false;
                _idleSpan = TimeSpan.Zero;
                _tickCounter = 0;
                AppEnvironment.Log("系统解锁/回连：" + e.Reason);
                Unlocked?.Invoke(this, EventArgs.Empty);
                break;

            default:
                break;
        }
    }

    /// <summary>释放：停止检测并退订系统事件（避免退出后事件泄漏）。</summary>
    public void Dispose()
    {
        Stop();
    }
}
