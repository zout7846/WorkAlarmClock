using System;
using System.Globalization;
using System.IO;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Services;

/// <summary>
/// 数据库备份服务：每天首次启动时用 VACUUM INTO 生成一致性快照，并按保留天数清理旧备份。
/// </summary>
public sealed class BackupService : IBackupService
{
    /// <summary>备份文件名前缀。</summary>
    private const string FilePrefix = "workalarm-";

    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;

    /// <summary>构造备份服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="storage">存储服务。</param>
    public BackupService(ISettingsService settings, IStorageService storage)
    {
        _settings = settings;
        _storage = storage;
    }

    /// <inheritdoc />
    public string BackupDirectory => Path.Combine(AppEnvironment.RootDirectory, "backups");

    /// <inheritdoc />
    public void RunIfDue(DateOnly today)
    {
        try
        {
            if (!_settings.Current.BackupEnabled)
            {
                return;
            }

            Directory.CreateDirectory(BackupDirectory);
            string path = Path.Combine(
                BackupDirectory,
                FilePrefix + today.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + ".db");

            if (!File.Exists(path))
            {
                _storage.BackupTo(path);
                AppEnvironment.Log("数据库已备份：" + path);
            }

            Prune();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("每日备份失败（不影响主流程）。", ex);
        }
    }

    /// <inheritdoc />
    public string? BackupNow()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            string path = Path.Combine(
                BackupDirectory,
                FilePrefix + DateTime.Now.ToString("yyyy.MM.dd_HHmmss", CultureInfo.InvariantCulture) + ".db");
            _storage.BackupTo(path);
            AppEnvironment.Log("手动备份完成：" + path);
            return path;
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("手动备份失败。", ex);
            return null;
        }
    }

    /// <summary>清理超过保留天数的备份文件。</summary>
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(BackupDirectory))
            {
                return;
            }

            int keepDays = Math.Clamp(_settings.Current.BackupKeepDays, 1, 365);
            DateTime threshold = DateTime.Now.AddDays(-keepDays);

            foreach (string file in Directory.GetFiles(BackupDirectory, FilePrefix + "*.db"))
            {
                if (File.GetLastWriteTime(file) >= threshold)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    AppEnvironment.Log("删除过期备份失败：" + file, ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("清理过期备份失败。", ex);
        }
    }
}
