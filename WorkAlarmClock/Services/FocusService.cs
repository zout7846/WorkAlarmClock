using System;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 番茄钟服务：专注 → 短休息（每 N 轮长休息）循环。秒级推进基于全局 <see cref="ITicker"/>，
/// 不额外起计时器，避免与主节拍重复驱动。
/// </summary>
public sealed class FocusService : IFocusService, IDisposable
{
    private readonly ITicker _ticker;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notification;

    private FocusPhase _phase = FocusPhase.Idle;
    private TimeSpan _remaining = TimeSpan.Zero;
    private int _rounds;

    /// <summary>构造番茄钟服务。</summary>
    /// <param name="ticker">全局时钟节拍。</param>
    /// <param name="settings">设置服务。</param>
    /// <param name="notification">通知服务。</param>
    public FocusService(ITicker ticker, ISettingsService settings, INotificationService notification)
    {
        _ticker = ticker;
        _settings = settings;
        _notification = notification;
        _ticker.Tick += OnTick;
    }

    /// <inheritdoc />
    public FocusPhase Phase => _phase;

    /// <inheritdoc />
    public TimeSpan Remaining => _remaining;

    /// <inheritdoc />
    public int CompletedRounds => _rounds;

    /// <inheritdoc />
    public bool IsFocusing => _phase == FocusPhase.Focus;

    /// <inheritdoc />
    public bool IsRunning => _phase != FocusPhase.Idle;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        AppSettings settings = _settings.Current;
        int minutes = Math.Max(1, settings.PomodoroFocusMinutes);
        _phase = FocusPhase.Focus;
        _remaining = TimeSpan.FromMinutes(minutes);
        _notification.ShowBubble("专注开始，接下来 " + minutes + " 分钟我们一起加油！", BubbleKind.Info);
        AppEnvironment.Log("番茄钟：开始专注 " + minutes + " 分钟。");
        Raise();
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _phase = FocusPhase.Idle;
        _remaining = TimeSpan.Zero;
        AppEnvironment.Log("番茄钟：已手动停止。");
        Raise();
    }

    /// <inheritdoc />
    public void Skip()
    {
        switch (_phase)
        {
            case FocusPhase.Focus:
                CompleteFocus();
                break;
            case FocusPhase.Break:
            case FocusPhase.LongBreak:
                Stop();
                break;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsRunning)
        {
            return;
        }

        // 每次 Tick 推进 1 秒；时钟被回拨时按 0 处理，绝不加时。
        _remaining -= TimeSpan.FromSeconds(1);
        if (_remaining > TimeSpan.Zero)
        {
            Raise();
            return;
        }

        _remaining = TimeSpan.Zero;
        if (_phase == FocusPhase.Focus)
        {
            CompleteFocus();
        }
        else
        {
            _notification.ShowBubble("休息结束，想继续就来一轮新的专注吧。", BubbleKind.Info);
            _notification.PlaySound(SoundKind.Weak);
            _phase = FocusPhase.Idle;
            Raise();
        }
    }

    /// <summary>专注结束：累计轮数并按设置进入短/长休息。</summary>
    private void CompleteFocus()
    {
        _rounds++;
        AppSettings settings = _settings.Current;
        int roundsPerLong = Math.Max(1, settings.PomodoroRounds);
        bool isLong = _rounds % roundsPerLong == 0;

        _notification.PlaySound(SoundKind.Strong);

        if (!settings.PomodoroAutoBreak)
        {
            _notification.ShowBubble("这一轮专注完成，休息一下吧！", BubbleKind.Info);
            _phase = FocusPhase.Idle;
            _remaining = TimeSpan.Zero;
            Raise();
            return;
        }

        int minutes = isLong
            ? Math.Max(1, settings.PomodoroLongBreakMinutes)
            : Math.Max(1, settings.PomodoroBreakMinutes);

        _phase = isLong ? FocusPhase.LongBreak : FocusPhase.Break;
        _remaining = TimeSpan.FromMinutes(minutes);
        _notification.ShowBubble(
            (isLong ? "完成 " + roundsPerLong + " 轮啦，来个长休息：" : "这一轮专注完成，休息 ")
            + minutes + " 分钟吧。",
            BubbleKind.Info);
        AppEnvironment.Log("番茄钟：进入" + (isLong ? "长" : "短") + "休息 " + minutes + " 分钟。");
        Raise();
    }

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>释放：退订时钟节拍。</summary>
    public void Dispose() => _ticker.Tick -= OnTick;
}
