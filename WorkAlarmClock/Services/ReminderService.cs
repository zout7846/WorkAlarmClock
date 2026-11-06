using System;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 提醒调度服务：下班前弱提醒（写日报）与强提醒（打卡）。
/// 提醒与主状态机解耦，只取决于时间点 + 工作日 + 开关，不受终态锁影响。
/// </summary>
public sealed class ReminderService : IReminderService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IScheduleService _schedule;
    private readonly ITicker _ticker;
    private readonly IStorageService _storage;

    private bool _started;
    private DateOnly _activeDate;
    private bool _reportGenerated;

    /// <summary>构造提醒服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="schedule">作息服务。</param>
    /// <param name="ticker">时钟节拍。</param>
    /// <param name="storage">存储服务（持久化提醒状态，避免重启重复提醒）。</param>
    public ReminderService(ISettingsService settings, IScheduleService schedule, ITicker ticker, IStorageService storage)
    {
        _settings = settings;
        _schedule = schedule;
        _ticker = ticker;
        _storage = storage;
        _activeDate = DateOnly.FromDateTime(ticker.Now.LocalDateTime);
    }

    /// <inheritdoc />
    public ReminderState State { get; } = new ReminderState();

    /// <summary>当日是否已生成过日报（UI 据此决定气泡按钮）。</summary>
    public bool ReportGenerated => _reportGenerated;

    /// <inheritdoc />
    public event EventHandler<ReminderKind>? ReminderDue;

    /// <inheritdoc />
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _ticker.Tick += OnTick;
        _started = true;
        AppEnvironment.Log("提醒调度已启动。");
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _ticker.Tick -= OnTick;
        _started = false;
    }

    /// <inheritdoc />
    public void InitializeState(DateOnly date)
    {
        _activeDate = date;
        ReminderState? saved = _storage.LoadReminderState(date);
        if (saved is null)
        {
            return;
        }

        State.WeakFired = saved.WeakFired;
        State.WeakIgnored = saved.WeakIgnored;
        State.WeakDeferredUntil = saved.WeakDeferredUntil;
        State.StrongFired = saved.StrongFired;
        State.StrongConfirmed = saved.StrongConfirmed;
        State.StrongRepeatCount = saved.StrongRepeatCount;
        AppEnvironment.Log("已读回当日提醒状态（弱提醒是否已触发=" + saved.WeakFired + "）。");
    }

    /// <inheritdoc />
    public void DeferWeak(TimeSpan delay)
    {
        State.WeakDeferredUntil = _ticker.Now + delay;
        State.WeakFired = false;
        State.WeakIgnored = false;
        PersistState();
        AppEnvironment.Log("弱提醒已推迟至 " + State.WeakDeferredUntil.Value.ToString("HH:mm:ss"));
    }

    /// <inheritdoc />
    public void IgnoreWeak()
    {
        State.WeakIgnored = true;
        State.WeakDeferredUntil = null;
        PersistState();
        AppEnvironment.Log("弱提醒当日已忽略。");
    }

    /// <inheritdoc />
    public void AcknowledgeStrong()
    {
        State.StrongConfirmed = true;
        State.StrongFired = true;
        PersistState();
        AppEnvironment.Log("强提醒已确认（已打卡）。");
    }

    /// <inheritdoc />
    public void OnReportGenerated()
    {
        _reportGenerated = true;
    }

    /// <inheritdoc />
    public void ResetForNewDay()
    {
        State.ResetForNewDay();
        _reportGenerated = false;
        _activeDate = DateOnly.FromDateTime(_ticker.Now.LocalDateTime);
        PersistState();
        AppEnvironment.Log("提醒状态已按新的一天重置。");
    }

    /// <summary>把内存态提醒状态 upsert 到 SQLite。</summary>
    private void PersistState() => _storage.SaveReminderState(_activeDate, State);

    /// <summary>释放：停止调度并退订 Ticker（避免退出后句柄/事件泄漏）。</summary>
    public void Dispose()
    {
        Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_started)
        {
            return;
        }

        DateTimeOffset now = _ticker.Now;
        (DateOnly date, TimeOnly _) = ScheduleService.Split(now);
        if (date != _activeDate)
        {
            ResetForNewDay();
        }

        if (!_schedule.IsWorkday(date))
        {
            return;
        }

        AppSettings settings = _settings.Current;
        DateTimeOffset workEnd = ScheduleService.ToLocal(date, settings.WorkEndTime);

        CheckWeak(now, settings, workEnd);
        CheckStrong(now, settings, workEnd);
    }

    /// <summary>弱提醒触发检查。</summary>
    private void CheckWeak(DateTimeOffset now, AppSettings settings, DateTimeOffset workEnd)
    {
        if (!settings.WeakReminderEnabled || State.WeakIgnored || State.WeakFired)
        {
            return;
        }

        if (State.WeakDeferredUntil.HasValue && now < State.WeakDeferredUntil.Value)
        {
            return;
        }

        DateTimeOffset dueAt = workEnd.AddMinutes(-settings.WeakReminderLeadMinutes);
        if (now < dueAt)
        {
            return;
        }

        State.WeakFired = true;
        State.WeakDeferredUntil = null;
        AppEnvironment.Log("触发弱提醒（写日报）。");
        ReminderDue?.Invoke(this, ReminderKind.Weak);
    }

    /// <summary>强提醒触发检查。</summary>
    private void CheckStrong(DateTimeOffset now, AppSettings settings, DateTimeOffset workEnd)
    {
        if (!settings.StrongReminderEnabled || State.StrongConfirmed || State.StrongFired)
        {
            return;
        }

        DateTimeOffset dueAt = workEnd.AddMinutes(-settings.StrongReminderLeadMinutes);
        if (now < dueAt)
        {
            return;
        }

        State.StrongFired = true;
        State.StrongRepeatCount = 0;
        AppEnvironment.Log("触发强提醒（打卡）。");
        ReminderDue?.Invoke(this, ReminderKind.Strong);
    }
}
