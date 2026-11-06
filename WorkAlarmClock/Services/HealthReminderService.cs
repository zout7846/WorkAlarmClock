using System;
using System.Windows.Threading;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 健康提醒服务：久坐与喝水提醒。仅在工作状态下累计时长，休息/午休/下班自动清零重新计时。
/// </summary>
public sealed class HealthReminderService : IHealthReminderService, IDisposable
{
    /// <summary>检查间隔（秒）。</summary>
    private const int TickSeconds = 20;

    private static readonly string[] SitMessages =
    {
        "已经连续坐了 {0} 分钟啦，起来走两步～",
        "腰要抗议了，站起来伸个懒腰吧！",
        "该活动一下啦，倒杯水顺便走两圈～"
    };

    private static readonly string[] DrinkMessages =
    {
        "喝口水吧，顺便看看远处放松一下眼睛。",
        "水分补充时间到，别等渴了才想起来～",
        "起来接杯水，走两步再回来。"
    };

    private readonly ISettingsService _settings;
    private readonly INotificationService _notification;
    private readonly ITimeTrackingService _timeTracking;
    private readonly IFocusService _focus;

    private readonly Random _random = new Random();
    private DispatcherTimer? _timer;
    private DateTimeOffset _sitSince;
    private DateTimeOffset _drinkSince;
    private bool _running;

    /// <summary>构造健康提醒服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="notification">通知服务。</param>
    /// <param name="timeTracking">工时服务（判断是否在工作）。</param>
    /// <param name="focus">番茄钟服务（专注期间不打扰）。</param>
    public HealthReminderService(
        ISettingsService settings,
        INotificationService notification,
        ITimeTrackingService timeTracking,
        IFocusService focus)
    {
        _settings = settings;
        _notification = notification;
        _timeTracking = timeTracking;
        _focus = focus;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        ResetAll();

        _timeTracking.StateChanged += OnStateChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += OnTick;
        _timer.Start();
        AppEnvironment.Log("健康提醒已启动（久坐/喝水）。");
    }

    /// <inheritdoc />
    public void Stop()
    {
        _running = false;
        _timeTracking.StateChanged -= OnStateChanged;

        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }
    }

    /// <inheritdoc />
    public void ResetSitTimer() => _sitSince = DateTimeOffset.Now;

    /// <summary>释放计时器。</summary>
    public void Dispose() => Stop();

    /// <summary>状态变化：离开工作状态立即清零，回到工作重新起算。</summary>
    private void OnStateChanged(object? sender, WorkState state) => ResetAll();

    private void ResetAll()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        _sitSince = now;
        _drinkSince = now;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_running)
        {
            return;
        }

        try
        {
            // 只在"工作中"计时；休息/午休/下班期间不打扰。
            if (_timeTracking.CurrentState != WorkState.Working)
            {
                ResetAll();
                return;
            }

            // 专注（番茄钟）期间不打断，且把久坐计时一并清零——休息阶段重新起算。
            if (_focus.IsFocusing)
            {
                ResetAll();
                return;
            }

            AppSettings settings = _settings.Current;
            DateTimeOffset now = DateTimeOffset.Now;

            if (IsDue(settings.HealthSitEnabled, settings.HealthSitIntervalMinutes, _sitSince, now))
            {
                int minutes = Math.Max(1, settings.HealthSitIntervalMinutes);
                _sitSince = now;
                AppEnvironment.Log("触发久坐提醒（连续工作 " + minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 分钟）。");
                _notification.ShowBubble(
                    string.Format(SitMessages[_random.Next(SitMessages.Length)], minutes),
                    BubbleKind.Info,
                    "已起身",
                    ResetSitTimer);
                _notification.PlaySound(SoundKind.Weak);
            }

            if (IsDue(settings.HealthDrinkEnabled, settings.HealthDrinkIntervalMinutes, _drinkSince, now))
            {
                _drinkSince = now;
                AppEnvironment.Log("触发喝水提醒。");
                _notification.ShowBubble(DrinkMessages[_random.Next(DrinkMessages.Length)], BubbleKind.Info);
                _notification.PlaySound(SoundKind.Weak);
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("健康提醒执行失败。", ex);
        }
    }

    private static bool IsDue(bool enabled, int intervalMinutes, DateTimeOffset since, DateTimeOffset now) =>
        enabled && intervalMinutes > 0 && now - since >= TimeSpan.FromMinutes(intervalMinutes);
}
