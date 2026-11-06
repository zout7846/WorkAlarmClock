using System;
using System.Collections.Generic;
using System.Linq;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 工时统计服务：状态机核心。
/// 时长永远是状态切换流水重算的派生值，切换即落盘，因此重启可完全幂等恢复。
/// 来源优先级：Manual &gt; Schedule &gt; Lock &gt; Idle。
/// </summary>
public sealed class TimeTrackingService : ITimeTrackingService
{
    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;
    private readonly IScheduleService _schedule;
    private readonly ITicker _ticker;

    private DayRecord _today = new DayRecord();
    private WorkState _currentState = WorkState.BeforeWork;
    private RestSource? _currentRestSource;
    private TimeSpan _workDuration = TimeSpan.Zero;
    private TimeSpan _restDuration = TimeSpan.Zero;
    private bool _locked;
    private bool _overtimeMode;
    private DateTimeOffset? _lastAppliedPoint;

    /// <summary>构造工时统计服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="storage">存储服务。</param>
    /// <param name="schedule">作息服务。</param>
    /// <param name="ticker">时钟节拍。</param>
    public TimeTrackingService(ISettingsService settings, IStorageService storage, IScheduleService schedule, ITicker ticker)
    {
        _settings = settings;
        _storage = storage;
        _schedule = schedule;
        _ticker = ticker;
        _ticker.Tick += OnTick;
    }

    /// <inheritdoc />
    public WorkState CurrentState => _currentState;

    /// <inheritdoc />
    public RestSource? CurrentRestSource => _currentRestSource;

    /// <inheritdoc />
    public bool ManualEnded => _today.ManualEnded;

    /// <inheritdoc />
    public DayRecord Today => _today;

    /// <inheritdoc />
    public TimeSpan WorkDuration => _workDuration;

    /// <inheritdoc />
    public TimeSpan RestDuration => _restDuration;

    /// <inheritdoc />
    public event EventHandler<WorkState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler? TasksChanged;

    /// <inheritdoc />
    public event EventHandler? DayRolled;

    /// <inheritdoc />
    public void LoadOrCreateToday()
    {
        DateTimeOffset now = _ticker.Now;
        DateOnly date = DateOnly.FromDateTime(now.LocalDateTime);
        _storage.EnsureArchive(date);

        DayRecord? loaded = _storage.LoadDay(date);
        if (loaded is null || loaded.Transitions.Count == 0)
        {
            _today = loaded ?? new DayRecord { Date = date };
            _today.Date = date;
            _today.ManualEnded = false;
            WorkState expected = _schedule.GetExpectedState(now);
            _today.Transitions.Add(new StateTransition
            {
                Time = now,
                From = WorkState.BeforeWork,
                To = expected,
                Source = TriggerSource.System,
                Note = "初始落位"
            });
            Persist();
        }
        else
        {
            _today = loaded;
        }

        Recompute(now);
        // 手动结束当天后又处于工作中 = 加班补记模式：检测类照常运作，Schedule 持续静默。
        _overtimeMode = _today.ManualEnded && _currentState == WorkState.Working;
        _lastAppliedPoint = LastPointAtOrBefore(now);
        AppEnvironment.Log(string.Concat(
            "当日记录已加载：日期=", _today.Date.ToString("yyyy-MM-dd"),
            "，流水=", _today.Transitions.Count.ToString(), " 条",
            "，当前状态=", ScheduleService.StateToText(_currentState, _currentRestSource)));
        StateChanged?.Invoke(this, _currentState);
    }

    /// <inheritdoc />
    public void ApplySchedulePoint(SchedulePointKind kind)
    {
        // 终态锁：手动结束当天后，作息自动切换全部静默。
        if (_today.ManualEnded)
        {
            return;
        }

        WorkState target = TargetStateOf(kind);
        if (target == _currentState)
        {
            // 已在目标状态：仅刷新休息来源（例如午休开始）。
            if (target == WorkState.Resting && kind == SchedulePointKind.LunchStart && _currentRestSource != RestSource.Lunch)
            {
                _currentRestSource = RestSource.Lunch;
            }

            return;
        }

        Transition(target, TriggerSource.Schedule, ScheduleService.PointKindToText(kind));
    }

    /// <inheritdoc />
    public bool TryManualSwitch(WorkState target, out string? error)
    {
        error = null;
        switch (target)
        {
            case WorkState.Working:
                if (_currentState == WorkState.Working)
                {
                    error = "当前已经处于工作中。";
                    return false;
                }

                Transition(WorkState.Working, TriggerSource.Manual, _today.ManualEnded ? "加班补记" : "手动开始工作");
                // 加班补记：重开检测类恢复逻辑（Schedule 仍静默）。
                _overtimeMode = _today.ManualEnded;
                return true;

            case WorkState.Resting:
                if (_currentState == WorkState.Resting)
                {
                    error = "当前已经处于休息中。";
                    return false;
                }

                if (_currentState == WorkState.OffWork)
                {
                    error = "今天已结束，加班请点击「开始工作」。";
                    return false;
                }

                Transition(WorkState.Resting, TriggerSource.Manual, "手动休息");
                return true;

            case WorkState.OffWork:
                if (_today.ManualEnded && _currentState == WorkState.OffWork)
                {
                    error = "今天已经结束啦。";
                    return false;
                }

                Transition(WorkState.OffWork, TriggerSource.Manual, "手动结束当天");
                _today.ManualEnded = true;
                Persist();
                return true;

            default:
                error = "不能手动切换到「待上班」。";
                return false;
        }
    }

    /// <inheritdoc />
    public void OnLocked()
    {
        _locked = true;
        if (!_settings.Current.LockAsRest || _currentState != WorkState.Working)
        {
            return;
        }

        // 终态锁下仅加班补记模式允许检测类继续运作。
        if (_today.ManualEnded && !_overtimeMode)
        {
            return;
        }

        Transition(WorkState.Resting, TriggerSource.Lock, "锁屏离开");
    }

    /// <inheritdoc />
    public void OnUnlocked()
    {
        _locked = false;
        if (_currentState != WorkState.Resting || _currentRestSource != RestSource.LockScreen)
        {
            return;
        }

        if (!CanAutoResume())
        {
            return;
        }

        Transition(WorkState.Working, TriggerSource.Lock, "解锁回岗");
    }

    /// <inheritdoc />
    public void OnIdleDetected()
    {
        // 锁屏期间抑制空闲检测，避免两种检测来源打架。
        if (_locked || _currentState != WorkState.Working)
        {
            return;
        }

        if (_today.ManualEnded && !_overtimeMode)
        {
            return;
        }

        Transition(WorkState.Resting, TriggerSource.Idle, "空闲");
    }

    /// <inheritdoc />
    public void OnActivityResumed()
    {
        if (_locked || _currentState != WorkState.Resting || _currentRestSource != RestSource.Idle)
        {
            return;
        }

        if (!CanAutoResume())
        {
            return;
        }

        Transition(WorkState.Working, TriggerSource.Idle, "回岗");
    }

    /// <inheritdoc />
    public void AddTask(string text) => AddTask(text, string.Empty);

    /// <inheritdoc />
    public void AddTask(string text, string tag)
    {
        string content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            return;
        }

        _today.Tasks.Add(new WorkTaskItem
        {
            Id = Guid.NewGuid(),
            Timestamp = _ticker.Now,
            Text = content,
            Tag = (tag ?? string.Empty).Trim()
        });
        Persist();
        TasksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool RemoveTask(Guid id)
    {
        WorkTaskItem? item = _today.Tasks.FirstOrDefault(t => t.Id == id);
        if (item is null)
        {
            return false;
        }

        _today.Tasks.Remove(item);
        Persist();
        TasksChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public void RefreshDurations() => Recompute(_ticker.Now);

    /// <summary>每 Tick：跨天切换、作息点兜底对账、时长重算。</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        DateTimeOffset now = _ticker.Now;
        DateOnly date = DateOnly.FromDateTime(now.LocalDateTime);
        if (_today.Date != date)
        {
            RollToNewDay(now, date);
            return;
        }

        ApplyMissedSchedulePoint(now);
        Recompute(now);
    }

    /// <summary>跨天：旧记录留置归档，加载/新建当日记录。</summary>
    private void RollToNewDay(DateTimeOffset now, DateOnly date)
    {
        Persist();
        _storage.EnsureArchive(date);
        AppEnvironment.Log("检测到跨天，切换到新的一天：" + date.ToString("yyyy-MM-dd"));

        DayRecord? loaded = _storage.LoadDay(date);
        if (loaded is null || loaded.Transitions.Count == 0)
        {
            _today = loaded ?? new DayRecord { Date = date };
            _today.Date = date;
            _today.ManualEnded = false;
            _today.Transitions.Add(new StateTransition
            {
                Time = now,
                From = WorkState.BeforeWork,
                To = _schedule.GetExpectedState(now),
                Source = TriggerSource.System,
                Note = "新的一天落位"
            });
            Persist();
        }
        else
        {
            _today = loaded;
        }

        Recompute(now);
        _overtimeMode = _today.ManualEnded && _currentState == WorkState.Working;
        _lastAppliedPoint = LastPointAtOrBefore(now);
        StateChanged?.Invoke(this, _currentState);
        TasksChanged?.Invoke(this, EventArgs.Empty);
        DayRolled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 兜底对账：若最近一个作息点尚未应用（睡眠唤醒、时钟跳变、错过 Tick），立即补上。
    /// </summary>
    private void ApplyMissedSchedulePoint(DateTimeOffset now)
    {
        if (_today.ManualEnded)
        {
            return;
        }

        DateTimeOffset? point = LastPointAtOrBefore(now);
        if (point is null || _lastAppliedPoint == point)
        {
            return;
        }

        _lastAppliedPoint = point;
        ApplySchedulePoint(PointKindOf(now));
    }

    /// <summary>写入一条流水并重算、落盘、广播。</summary>
    private void Transition(WorkState target, TriggerSource source, string? note)
    {
        DateTimeOffset now = _ticker.Now;
        _today.Transitions.Add(new StateTransition
        {
            Time = now,
            From = _currentState,
            To = target,
            Source = source,
            Note = note
        });

        Recompute(now);
        Persist();
        StateChanged?.Invoke(this, _currentState);
    }

    /// <summary>重放流水，刷新状态、休息来源与时长。</summary>
    private void Recompute(DateTimeOffset now)
    {
        ReplayResult result = ReplayCalculator.Replay(_today.Transitions, now, _settings.Current);
        _currentState = result.State;
        _currentRestSource = result.RestSourceValue;
        _workDuration = result.Work;
        _restDuration = result.Rest;
    }

    /// <summary>原子写当日记录。</summary>
    private void Persist() => _storage.SaveDay(_today);

    /// <summary>是否允许检测类来源自动恢复工作。</summary>
    private bool CanAutoResume()
    {
        if (_today.ManualEnded && !_overtimeMode)
        {
            return false;
        }

        if (_overtimeMode)
        {
            // 加班时段：作息期望状态已不可靠，检测类恢复照常。
            return true;
        }

        // 处于工作时段（工作日、非午休、未下班）才自动恢复。
        return _schedule.GetExpectedState(_ticker.Now) == WorkState.Working;
    }

    /// <summary>取当前时刻之前（含当前）最近的作息点时刻。</summary>
    private DateTimeOffset? LastPointAtOrBefore(DateTimeOffset now)
    {
        (DateOnly date, TimeOnly time) = ScheduleService.Split(now);
        SchedulePoint? found = null;
        foreach (SchedulePoint point in _schedule.GetTodayPoints(date))
        {
            if (point.Time <= time)
            {
                found = point;
            }
        }

        return found is null ? null : ScheduleService.ToLocal(date, found.Value.Time);
    }

    /// <summary>取当前时刻所在作息点的类型。</summary>
    private SchedulePointKind PointKindOf(DateTimeOffset now)
    {
        (DateOnly date, TimeOnly time) = ScheduleService.Split(now);
        SchedulePointKind kind = SchedulePointKind.WorkStart;
        foreach (SchedulePoint point in _schedule.GetTodayPoints(date))
        {
            if (point.Time <= time)
            {
                kind = point.Kind;
            }
        }

        return kind;
    }

    /// <summary>作息点类型对应的目标状态。</summary>
    private static WorkState TargetStateOf(SchedulePointKind kind)
    {
        return kind switch
        {
            SchedulePointKind.WorkStart => WorkState.Working,
            SchedulePointKind.LunchStart => WorkState.Resting,
            SchedulePointKind.LunchEnd => WorkState.Working,
            _ => WorkState.OffWork
        };
    }
}
