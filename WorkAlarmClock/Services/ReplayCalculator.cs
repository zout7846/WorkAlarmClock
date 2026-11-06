using System;
using System.Collections.Generic;
using System.Linq;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 流水重放结果（时长永远是派生值）。
/// </summary>
/// <param name="State">重放后的当前状态。</param>
/// <param name="RestSourceValue">当前休息来源（非休息状态为 null）。</param>
/// <param name="Work">累计工作时长。</param>
/// <param name="Rest">累计休息时长。</param>
/// <param name="Lunch">午休时长（休息中来源为午休的部分）。</param>
public readonly record struct ReplayResult(WorkState State, RestSource? RestSourceValue, TimeSpan Work, TimeSpan Rest, TimeSpan Lunch);

/// <summary>
/// 状态切换流水的幂等重算器：工作/休息时长完全由流水派生，永不单独存储。
/// TimeTrackingService 与 StatisticsService 共用，避免重复实现。
/// </summary>
public static class ReplayCalculator
{
    /// <summary>
    /// 重放流水，得到当前状态与累计时长。
    /// </summary>
    /// <param name="transitions">状态切换流水（内部按时间升序处理）。</param>
    /// <param name="now">当前时刻（用于计算开放段）。</param>
    /// <param name="settings">当前设置（决定是否把空闲计入工时）。</param>
    /// <returns>重放结果。</returns>
    public static ReplayResult Replay(IReadOnlyList<StateTransition> transitions, DateTimeOffset now, AppSettings settings)
    {
        if (transitions is null || transitions.Count == 0)
        {
            return new ReplayResult(WorkState.BeforeWork, null, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        }

        List<StateTransition> ordered = transitions.OrderBy(t => t.Time).ToList();

        WorkState previousState = WorkState.BeforeWork;
        DateTimeOffset previousTime = ordered[0].Time;
        RestSource? previousRestSource = null;
        TimeSpan work = TimeSpan.Zero;
        TimeSpan rest = TimeSpan.Zero;
        TimeSpan lunch = TimeSpan.Zero;

        foreach (StateTransition transition in ordered)
        {
            // 负区间（系统时钟回拨）clamp 为 0，保证任何情况下不会扣减时长。
            Accumulate(previousState, previousRestSource, transition.Time - previousTime, settings, ref work, ref rest, ref lunch);
            previousState = transition.To;
            previousTime = transition.Time;
            previousRestSource = ResolveRestSource(transition, settings);
        }

        Accumulate(previousState, previousRestSource, now - previousTime, settings, ref work, ref rest, ref lunch);

        return new ReplayResult(
            previousState,
            previousState == WorkState.Resting ? previousRestSource : null,
            work,
            rest,
            lunch);
    }

    /// <summary>
    /// 解析一条流水对应的休息来源（仅当目标状态为休息时有意义）。
    /// </summary>
    /// <param name="transition">状态切换流水。</param>
    /// <param name="settings">当前设置（用于系统落位时判断是否为午休）。</param>
    /// <returns>休息来源；非休息目标返回 null。</returns>
    public static RestSource? ResolveRestSource(StateTransition transition, AppSettings settings)
    {
        if (transition.To != WorkState.Resting)
        {
            return null;
        }

        return transition.Source switch
        {
            TriggerSource.Schedule => RestSource.Lunch,
            TriggerSource.Lock => RestSource.LockScreen,
            TriggerSource.Idle => RestSource.Idle,
            TriggerSource.Manual => RestSource.Manual,
            _ => IsWithinLunch(transition.Time, settings) ? RestSource.Lunch : RestSource.Idle
        };
    }

    /// <summary>判断某时刻是否处于午休时段内。</summary>
    /// <param name="time">时刻。</param>
    /// <param name="settings">当前设置。</param>
    /// <returns>true 表示处于午休时段。</returns>
    public static bool IsWithinLunch(DateTimeOffset time, AppSettings settings)
    {
        if (!settings.LunchEnabled)
        {
            return false;
        }

        TimeOnly current = TimeOnly.FromDateTime(time.LocalDateTime);
        return current >= settings.LunchStartTime && current < settings.LunchEndTime;
    }

    /// <summary>把一段时间累加到工作或休息时长上。</summary>
    private static void Accumulate(
        WorkState state,
        RestSource? restSource,
        TimeSpan span,
        AppSettings settings,
        ref TimeSpan work,
        ref TimeSpan rest,
        ref TimeSpan lunch)
    {
        if (span <= TimeSpan.Zero)
        {
            return;
        }

        if (state == WorkState.Working)
        {
            work += span;
            return;
        }

        if (state == WorkState.Resting)
        {
            // 开关：空闲段可计入工时；锁屏段始终计入休息（PRD 已定）。
            if (settings.CountIdleAsWork && restSource == RestSource.Idle)
            {
                work += span;
            }
            else
            {
                rest += span;
            }

            if (restSource == RestSource.Lunch)
            {
                lunch += span;
            }
        }
    }
}
