using System;
using System.IO;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 设置服务：加载（损坏回默认）、校验、保存与变更广播。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly IStorageService _storage;
    private AppSettings _current;

    /// <summary>构造并立即加载设置。</summary>
    /// <param name="storage">存储服务。</param>
    public SettingsService(IStorageService storage)
    {
        _storage = storage;
        _current = Load();
    }

    /// <inheritdoc />
    public AppSettings Current => _current;

    /// <inheritdoc />
    public event EventHandler? SettingsChanged;

    /// <inheritdoc />
    public ValidationResult Validate(AppSettings candidate)
    {
        if (candidate is null)
        {
            return new ValidationResult(false, "设置内容不能为空。");
        }

        // 1. 下班必须晚于上班。
        if (candidate.WorkEndTime <= candidate.WorkStartTime)
        {
            return new ValidationResult(false, "下班时间必须晚于上班时间。");
        }

        // 2. 午休区间合法。
        if (candidate.LunchEnabled)
        {
            if (candidate.LunchStartTime < candidate.WorkStartTime)
            {
                return new ValidationResult(false, "午休开始时间不能早于上班时间。");
            }

            if (candidate.LunchEndTime <= candidate.LunchStartTime)
            {
                return new ValidationResult(false, "午休结束时间必须晚于午休开始时间。");
            }

            if (candidate.LunchEndTime > candidate.WorkEndTime)
            {
                return new ValidationResult(false, "午休结束时间不能晚于下班时间。");
            }
        }

        // 3. 提醒提前量 0 – 120 分钟。
        if (candidate.WeakReminderLeadMinutes < 0 || candidate.WeakReminderLeadMinutes > 120)
        {
            return new ValidationResult(false, "弱提醒提前量需在 0 到 120 分钟之间。");
        }

        if (candidate.StrongReminderLeadMinutes < 0 || candidate.StrongReminderLeadMinutes > 120)
        {
            return new ValidationResult(false, "强提醒提前量需在 0 到 120 分钟之间。");
        }

        // 4. 空闲阈值 1 – 120 分钟。
        if (candidate.IdleThresholdMinutes < 1 || candidate.IdleThresholdMinutes > 120)
        {
            return new ValidationResult(false, "空闲阈值需在 1 到 120 分钟之间。");
        }

        // 5. 桌宠透明度 0.2 – 1.0。
        if (double.IsNaN(candidate.PetOpacity) || candidate.PetOpacity < 0.2 || candidate.PetOpacity > 1.0)
        {
            return new ValidationResult(false, "桌宠透明度需在 0.2 到 1.0 之间。");
        }

        // 6. 汇报目录合法。
        if (!string.IsNullOrWhiteSpace(candidate.ReportDirectory) && HasInvalidPathChars(candidate.ReportDirectory))
        {
            return new ValidationResult(false, "汇报目录路径包含非法字符。");
        }

        // 7. 强提醒重复策略。
        if (candidate.StrongRepeatIntervalSeconds < 1 || candidate.StrongRepeatIntervalSeconds > 600)
        {
            return new ValidationResult(false, "强提醒重复间隔需在 1 到 600 秒之间。");
        }

        if (candidate.StrongRepeatMaxCount < 1 || candidate.StrongRepeatMaxCount > 10)
        {
            return new ValidationResult(false, "强提醒重复次数需在 1 到 10 次之间。");
        }

        // 8. 全非工作日：允许保存，但给出警告。
        if (candidate.Workdays is { Length: 7 })
        {
            bool any = false;
            for (int i = 0; i < 7; i++)
            {
                if (candidate.Workdays[i])
                {
                    any = true;
                    break;
                }
            }

            if (!any)
            {
                return new ValidationResult(true, "未选择任何工作日，将不会自动计时与提醒。");
            }
        }

        return new ValidationResult(true, null);
    }

    /// <inheritdoc />
    public ValidationResult TryUpdate(AppSettings candidate)
    {
        ValidationResult result = Validate(candidate);
        if (!result.Ok)
        {
            return result;
        }

        _current = candidate;
        _storage.SaveSettings(_current);
        if (!string.IsNullOrEmpty(result.Error))
        {
            AppEnvironment.LogWarning("设置保存时出现警告：" + result.Error);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>加载设置；无记录或校验失败时回退默认值并落库。</summary>
    /// <returns>设置实例。</returns>
    private AppSettings Load()
    {
        AppSettings settings = _storage.LoadSettings();
        ValidationResult validation = Validate(settings);
        if (!validation.Ok)
        {
            AppEnvironment.LogWarning("设置校验失败，已回退默认设置：" + validation.Error);
            settings = AppSettings.CreateDefault();
            _storage.SaveSettings(settings);
            return settings;
        }

        // 首启或迁移场景：确保当前有效设置已持久化（幂等）。
        _storage.SaveSettings(settings);
        return settings;
    }

    /// <summary>判断路径是否包含非法字符。</summary>
    /// <param name="path">路径。</param>
    /// <returns>true 表示非法。</returns>
    private static bool HasInvalidPathChars(string path)
    {
        char[] invalid = Path.GetInvalidPathChars();
        foreach (char c in invalid)
        {
            if (path.IndexOf(c) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
