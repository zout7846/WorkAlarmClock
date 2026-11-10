using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Services;
using WF = System.Windows.Forms;

namespace WorkAlarmClock.ViewModels;

/// <summary>
/// 设置面板视图模型：草稿-应用两段式（深拷贝 Current → 编辑 → 校验 → TryUpdate）。
/// 时间输入经 TimeComboBox 直接绑定 Draft 的 TimeOnly；热键为 5 组可自定义组合键（组内唯一）。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _current;
    private string _weakLeadText;
    private string _strongLeadText;
    private string _strongRepeatIntervalText;
    private string _strongRepeatMaxCountText;
    private string _idleThresholdText;
    private string _validationError;
    private double _petOpacity;

    /// <summary>构造设置视图模型（草稿 = 当前设置的深拷贝）。</summary>
    public SettingsViewModel()
    {
        _current = AppServices.Settings.Current;
        Draft = _current.Clone();

        _weakLeadText = Draft.WeakReminderLeadMinutes.ToString(CultureInfo.InvariantCulture);
        _strongLeadText = Draft.StrongReminderLeadMinutes.ToString(CultureInfo.InvariantCulture);
        _strongRepeatIntervalText = Draft.StrongRepeatIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        _strongRepeatMaxCountText = Draft.StrongRepeatMaxCount.ToString(CultureInfo.InvariantCulture);
        _idleThresholdText = Draft.IdleThresholdMinutes.ToString(CultureInfo.InvariantCulture);
        _petOpacity = Draft.PetOpacity;
        _validationError = string.Empty;

        SaveCommand = new RelayCommand(Save, () => string.IsNullOrEmpty(_validationError));
        CancelCommand = new RelayCommand(Cancel);
        BrowseDirectoryCommand = new RelayCommand(BrowseDirectory);
        BrowseScreenshotDirectoryCommand = new RelayCommand(BrowseScreenshotDirectory);
        BackupNowCommand = new RelayCommand(BackupNow);
        ResetTemplateCommand = new RelayCommand(ResetTemplate);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        ResetPetImageCommand = new RelayCommand(() => PetImagePath = string.Empty);
        ResetFooterCommand = new RelayCommand(ResetFooter);
        RelocateCommand = new RelayCommand(RelocateByIp);
        ApplyCoordinatesCommand = new RelayCommand(ApplyCoordinates);
        _selectedCity = ResolveCityPreset();
    }

    /// <summary>内置城市列表（手动指定地区用）。</summary>
    public IReadOnlyList<CityPreset> CityOptions => CityPresets.All;

    /// <summary>手动指定地区：开启后不再用 IP 定位。</summary>
    public bool UseManualLocation
    {
        get => Draft.WeatherManualLocation;
        set
        {
            if (Draft.WeatherManualLocation == value)
            {
                return;
            }

            Draft.WeatherManualLocation = value;
            if (value)
            {
                Draft.WeatherAutoLocate = false;
                OnPropertyChanged(nameof(Draft));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAutoLocate));
            AutoSaveWeather();
        }
    }

    /// <summary>IP 自动定位是否可用（手动指定时禁用）。</summary>
    public bool CanAutoLocate => !Draft.WeatherManualLocation;

    /// <summary>选中的城市；选择后立即写入经纬度并保存。</summary>
    public CityPreset? SelectedCity
    {
        get => _selectedCity;
        set
        {
            if (ReferenceEquals(_selectedCity, value))
            {
                return;
            }

            _selectedCity = value;
            OnPropertyChanged();

            if (value is null)
            {
                return;
            }

            Draft.WeatherCity = value.Name;
            Draft.WeatherLatitude = value.Latitude;
            Draft.WeatherLongitude = value.Longitude;
            Draft.WeatherManualLocation = true;
            Draft.WeatherAutoLocate = false;
            OnPropertyChanged(nameof(Draft));
            OnPropertyChanged(nameof(UseManualLocation));
            OnPropertyChanged(nameof(CanAutoLocate));
            AutoSaveWeather();
        }
    }

    /// <summary>当前地区说明（城市 + 经纬度）。</summary>
    public string WeatherLocationText =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} · {1:0.####}, {2:0.####}",
            string.IsNullOrWhiteSpace(Draft.WeatherCity) ? "未设置" : Draft.WeatherCity,
            Draft.WeatherLatitude,
            Draft.WeatherLongitude);

    /// <summary>按 IP 重新定位命令。</summary>
    public RelayCommand RelocateCommand { get; }

    /// <summary>应用手填坐标命令。</summary>
    public RelayCommand ApplyCoordinatesCommand { get; }

    /// <summary>应用手填经纬度（自动保存并刷新天气）。</summary>
    private void ApplyCoordinates()
    {
        Draft.WeatherManualLocation = true;
        Draft.WeatherAutoLocate = false;
        OnPropertyChanged(nameof(UseManualLocation));
        OnPropertyChanged(nameof(Draft));
        _selectedCity = ResolveCityPreset();
        OnPropertyChanged(nameof(SelectedCity));
        AutoSaveWeather();
    }

    /// <summary>设置草稿（布尔/数组/目录/时间/热键/页脚等直接绑定此对象）。</summary>
    public AppSettings Draft { get; }

    /// <summary>校验错误文案（空 = 无错误），窗口顶部红字显示。</summary>
    public string ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>弱提醒提前量文本（分钟）。</summary>
    public string WeakLeadText
    {
        get => _weakLeadText;
        set { SetProperty(ref _weakLeadText, value); ValidateLive(); }
    }

    /// <summary>强提醒提前量文本（分钟）。</summary>
    public string StrongLeadText
    {
        get => _strongLeadText;
        set { SetProperty(ref _strongLeadText, value); ValidateLive(); }
    }

    /// <summary>强提醒重复间隔文本（秒）。</summary>
    public string StrongRepeatIntervalText
    {
        get => _strongRepeatIntervalText;
        set { SetProperty(ref _strongRepeatIntervalText, value); ValidateLive(); }
    }

    /// <summary>强提醒重复次数文本。</summary>
    public string StrongRepeatMaxCountText
    {
        get => _strongRepeatMaxCountText;
        set { SetProperty(ref _strongRepeatMaxCountText, value); ValidateLive(); }
    }

    /// <summary>空闲阈值文本（分钟）。</summary>
    public string IdleThresholdText
    {
        get => _idleThresholdText;
        set { SetProperty(ref _idleThresholdText, value); ValidateLive(); }
    }

    /// <summary>桌宠透明度（拖动即时生效，保存才持久化）。</summary>
    public double PetOpacity
    {
        get => _petOpacity;
        set
        {
            if (SetProperty(ref _petOpacity, value))
            {
                Draft.PetOpacity = value;
                AppServices.RaiseLiveOpacityPreview(value);
            }
        }
    }

    /// <summary>开始工作热键组合（读写均落到草稿）。</summary>
    public HotkeyCombo HotkeyStartWork
    {
        get => Draft.Hotkeys.StartWork;
        set { Draft.Hotkeys.StartWork = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>开始休息热键组合。</summary>
    public HotkeyCombo HotkeyStartRest
    {
        get => Draft.Hotkeys.StartRest;
        set { Draft.Hotkeys.StartRest = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>结束当天热键组合。</summary>
    public HotkeyCombo HotkeyEndDay
    {
        get => Draft.Hotkeys.EndDay;
        set { Draft.Hotkeys.EndDay = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>生成工作汇报热键组合。</summary>
    public HotkeyCombo HotkeyGenerateReport
    {
        get => Draft.Hotkeys.GenerateReport;
        set { Draft.Hotkeys.GenerateReport = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>显示/隐藏桌宠热键组合。</summary>
    public HotkeyCombo HotkeyTogglePet
    {
        get => Draft.Hotkeys.TogglePet;
        set { Draft.Hotkeys.TogglePet = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>显示 / 隐藏桌宠热键组合。</summary>
    public HotkeyCombo HotkeyToggleVisible
    {
        get => Draft.Hotkeys.ToggleVisible;
        set { Draft.Hotkeys.ToggleVisible = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>区域截图热键组合。</summary>
    public HotkeyCombo HotkeyScreenshot
    {
        get => Draft.Hotkeys.Screenshot;
        set { Draft.Hotkeys.Screenshot = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>开始/停止专注热键组合。</summary>
    public HotkeyCombo HotkeyFocus
    {
        get => Draft.Hotkeys.Focus;
        set { Draft.Hotkeys.Focus = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>变量名生成器热键组合。</summary>
    public HotkeyCombo HotkeyVariableNameGenerator
    {
        get => Draft.Hotkeys.VariableNameGenerator;
        set { Draft.Hotkeys.VariableNameGenerator = value; OnPropertyChanged(); ValidateLive(); }
    }

    /// <summary>浏览截图目录命令。</summary>
    public RelayCommand BrowseScreenshotDirectoryCommand { get; }

    /// <summary>立即备份命令。</summary>
    public RelayCommand BackupNowCommand { get; }

    /// <summary>恢复默认日报模板命令。</summary>
    public RelayCommand ResetTemplateCommand { get; }

    /// <summary>导出 CSV 命令。</summary>
    public RelayCommand ExportCsvCommand { get; }

    /// <summary>恢复默认桌宠素材命令。</summary>
    public RelayCommand ResetPetImageCommand { get; }

    /// <summary>自定义桌宠图片路径（空 = 内置素材）。</summary>
    public string PetImagePath
    {
        get => Draft.PetImagePath;
        set
        {
            if (Draft.PetImagePath == value)
            {
                return;
            }

            Draft.PetImagePath = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Draft));
            OnPropertyChanged(nameof(PetImagePreview));
            OnPropertyChanged(nameof(PetImagePreviewText));
        }
    }

    /// <summary>桌宠素材预览图。</summary>
    public ImageSource? PetImagePreview => LoadPreview(Draft.PetImagePath);

    /// <summary>桌宠素材显示名。</summary>
    public string PetImagePreviewText =>
        string.IsNullOrWhiteSpace(Draft.PetImagePath)
            ? "内置素材"
            : System.IO.Path.GetFileName(Draft.PetImagePath);

    /// <summary>加载预览图（自定义文件失败时回退内置素材）。</summary>
    private static ImageSource? LoadPreview(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
        {
            try
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // 损坏图片回退内置素材。
            }
        }

        return new BitmapImage(new Uri("pack://application:,,,/Assets/pet.png"));
    }

    /// <summary>日报输出格式选项。</summary>
    public IReadOnlyList<ReportFormatOption> ReportFormatOptions { get; } = new[]
    {
        new ReportFormatOption(0, "仅 txt"),
        new ReportFormatOption(1, "仅 md"),
        new ReportFormatOption(2, "txt + md")
    };

    /// <summary>桌宠形象选项。</summary>
    public IReadOnlyList<PetSpeciesOption> PetSpeciesOptions { get; } = new[]
    {
        new PetSpeciesOption(0, "小猫"),
        new PetSpeciesOption(1, "小狗"),
        new PetSpeciesOption(2, "狐狸")
    };

    /// <summary>当前选中的桌宠形象。</summary>
    public int SelectedSpecies
    {
        get => Draft.PetSpecies;
        set
        {
            if (Draft.PetSpecies == value)
            {
                return;
            }

            Draft.PetSpecies = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Draft));
        }
    }
    /// <summary>桌宠皮肤（发色）选项。</summary>
    public IReadOnlyList<PetSkinOption> PetSkinOptions { get; } = new[]
    {
        new PetSkinOption(0, "蓝发", "#FF4A6FD8"),
        new PetSkinOption(1, "粉发", "#FFE86A8E"),
        new PetSkinOption(2, "绿发", "#FF3FA98A"),
        new PetSkinOption(3, "暖棕", "#FFA85A2A")
    };

    /// <summary>当前选中的桌宠皮肤（写入草稿，保存后生效）。</summary>
    public int SelectedSkin
    {
        get => Draft.PetSkin;
        set
        {
            if (Draft.PetSkin == value)
            {
                return;
            }

            Draft.PetSkin = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Draft));
        }
    }

    /// <summary>保存命令（有校验错误时禁用）。</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>取消命令（还原实时透明度并关窗）。</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>浏览汇报目录命令。</summary>
    public RelayCommand BrowseDirectoryCommand { get; }

    private CityPreset? _selectedCity;

    /// <summary>按城市名匹配预设，匹配不到则返回 null。</summary>
    private CityPreset? ResolveCityPreset()
    {
        if (string.IsNullOrWhiteSpace(Draft.WeatherCity))
        {
            return null;
        }

        foreach (CityPreset preset in CityPresets.All)
        {
            if (string.Equals(preset.Name, Draft.WeatherCity, StringComparison.Ordinal))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>立即落盘天气地区（自动保存，不等「保存」按钮）。</summary>
    private void AutoSaveWeather()
    {
        try
        {
            AppServices.Settings.TryUpdate(Draft.Clone());
            _ = AppServices.Weather.RefreshAsync();
            OnPropertyChanged(nameof(WeatherLocationText));
        }
        catch (Exception ex)
        {
            Infrastructure.AppEnvironment.Log("保存天气地区失败。", ex);
        }
    }

    /// <summary>按 IP 重新定位并同步回草稿。</summary>
    private async void RelocateByIp()
    {
        try
        {
            Draft.WeatherManualLocation = false;
            Draft.WeatherAutoLocate = true;
            OnPropertyChanged(nameof(UseManualLocation));
            OnPropertyChanged(nameof(Draft));

            await AppServices.Weather.LocateAsync(WeatherLocateTrigger.Manual);

            AppSettings saved = AppServices.Settings.Current;
            Draft.WeatherCity = saved.WeatherCity;
            Draft.WeatherLatitude = saved.WeatherLatitude;
            Draft.WeatherLongitude = saved.WeatherLongitude;
            Draft.WeatherManualLocation = saved.WeatherManualLocation;
            Draft.WeatherAutoLocate = saved.WeatherAutoLocate;
            Draft.WeatherLocatedAt = saved.WeatherLocatedAt;
            _selectedCity = ResolveCityPreset();
            OnPropertyChanged(nameof(SelectedCity));
            OnPropertyChanged(nameof(Draft));
            OnPropertyChanged(nameof(WeatherLocationText));
            OnPropertyChanged(nameof(UseManualLocation));
            OnPropertyChanged(nameof(CanAutoLocate));
        }
        catch (Exception ex)
        {
            Infrastructure.AppEnvironment.Log("按 IP 重新定位失败。", ex);
        }
    }

    /// <summary>清空日报页脚命令。</summary>
    public RelayCommand ResetFooterCommand { get; }

    /// <summary>窗口请求关闭事件。</summary>
    public event EventHandler? CloseRequested;

    private void Save()
    {
        if (!TryApplyToDraft(out string? error))
        {
            ValidationError = error ?? "设置内容有误。";
            return;
        }

        ValidationResult result = AppServices.Settings.TryUpdate(Draft);
        if (!result.Ok)
        {
            ValidationError = result.Error ?? "设置校验失败。";
            return;
        }

        ValidationError = string.Empty;
        PageNotifications.Publish("设置已保存", "新设置已生效。快捷键注册结果可在消息记录中查看。");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel()
    {
        // 取消：实时预览的透明度还原为当前生效值。
        AppServices.RaiseLiveOpacityPreview(_current.PetOpacity);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BrowseDirectory()
    {
        try
        {
            using WF.FolderBrowserDialog dialog = new WF.FolderBrowserDialog
            {
                Description = "选择日报保存目录（留空使用默认目录）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            string initial = string.IsNullOrWhiteSpace(Draft.ReportDirectory)
                ? AppServices.Report.ResolveReportDirectory()
                : Draft.ReportDirectory;
            if (Directory.Exists(initial))
            {
                dialog.SelectedPath = initial;
            }

            if (dialog.ShowDialog() == WF.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                Draft.ReportDirectory = dialog.SelectedPath;
                OnPropertyChanged(nameof(Draft));
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("打开目录选择对话框失败。", ex);
        }
    }

    private void ResetFooter()
    {
        Draft.ReportFooter = string.Empty;
        OnPropertyChanged(nameof(Draft));
    }

    /// <summary>选择截图保存目录。</summary>
    private void BrowseScreenshotDirectory()
    {
        try
        {
            using WF.FolderBrowserDialog dialog = new WF.FolderBrowserDialog
            {
                Description = "选择截图保存目录（留空使用默认目录）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            string initial = string.IsNullOrWhiteSpace(Draft.ScreenshotDirectory)
                ? AppServices.Screenshot.ResolveScreenshotDirectory()
                : Draft.ScreenshotDirectory;
            if (Directory.Exists(initial))
            {
                dialog.SelectedPath = initial;
            }

            if (dialog.ShowDialog() == WF.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                Draft.ScreenshotDirectory = dialog.SelectedPath;
                OnPropertyChanged(nameof(Draft));
            }
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("打开截图目录选择对话框失败。", ex);
        }
    }

    /// <summary>立即生成一份数据库备份。</summary>
    private void BackupNow()
    {
        string? path = AppServices.Backup.BackupNow();
        AppServices.Notification.ShowBubble(
            path is null ? "备份失败，请查看日志。" : "已备份到：" + Path.GetFileName(path),
            BubbleKind.Info,
            path is null ? null : "打开目录",
            path is null ? null : () => OpenPath(AppServices.Backup.BackupDirectory));
    }

    /// <summary>恢复内置日报模板。</summary>
    private void ResetTemplate()
    {
        Draft.ReportTemplate = string.Empty;
        OnPropertyChanged(nameof(Draft));
    }

    /// <summary>导出工时 / 事项 / 日报清单为 CSV。</summary>
    private void ExportCsv()
    {
        IReadOnlyList<string> files = AppServices.Export.ExportAll();
        if (files.Count == 0)
        {
            AppServices.Notification.ShowBubble("导出失败，请查看日志。", BubbleKind.Info);
            return;
        }

        string directory = System.IO.Path.GetDirectoryName(files[0]) ?? AppServices.Export.ExportRoot;
        AppServices.Notification.ShowBubble(
            "已导出 " + files.Count + " 个 CSV 文件。",
            BubbleKind.Info,
            "打开目录",
            () => OpenPath(directory));
    }

    private static void OpenPath(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("打开目录失败：" + directory, ex);
        }
    }

    /// <summary>输入变化即时校验（红字提示 + 禁用保存）。</summary>
    private void ValidateLive()
    {
        _ = TryApplyToDraft(out string? error);
        ValidationError = error ?? string.Empty;
    }

    /// <summary>把全部文本输入解析进草稿并做区间/热键校验；任何一项失败即返回错误。</summary>
    private bool TryApplyToDraft(out string? error)
    {
        if (!TryParseInt(WeakLeadText, out int weakLead))
        {
            error = "弱提醒提前量应为数字（分钟）。";
            return false;
        }

        if (!TryParseInt(StrongLeadText, out int strongLead))
        {
            error = "强提醒提前量应为数字（分钟）。";
            return false;
        }

        if (!TryParseInt(StrongRepeatIntervalText, out int repeatInterval))
        {
            error = "强提醒重复间隔应为数字（秒）。";
            return false;
        }

        if (!TryParseInt(StrongRepeatMaxCountText, out int repeatMax))
        {
            error = "强提醒重复次数应为数字。";
            return false;
        }

        if (!TryParseInt(IdleThresholdText, out int idleThreshold))
        {
            error = "空闲阈值应为数字（分钟）。";
            return false;
        }

        // 时间输入已由 TimeComboBox 直接写入 Draft.TimeOnly，这里仅做区间合理性校验。
        TimeOnly workStart = Draft.WorkStartTime;
        TimeOnly workEnd = Draft.WorkEndTime;
        if (workEnd <= workStart)
        {
            error = "下班时间必须晚于上班时间。";
            return false;
        }

        if (Draft.LunchEnabled)
        {
            TimeOnly lunchStart = Draft.LunchStartTime;
            TimeOnly lunchEnd = Draft.LunchEndTime;
            if (lunchEnd <= lunchStart)
            {
                error = "午休结束时间必须晚于开始时间。";
                return false;
            }

            if (lunchStart < workStart || lunchEnd > workEnd)
            {
                error = "午休时间段应落在工作时段内。";
                return false;
            }
        }

        // 热键组内唯一性校验：无空键、无重复组合。
        HotkeyCombo[] combos = Draft.Hotkeys.Enumerate().Select(item => item.Combo).ToArray();
        for (int i = 0; i < combos.Length; i++)
        {
            if (combos[i].VirtualKeyCode == 0)
            {
                error = "存在未设置的热键组合，请点击「录制」完成设置。";
                return false;
            }

            for (int j = i + 1; j < combos.Length; j++)
            {
                if (combos[i].Equals(combos[j]))
                {
                    error = "存在重复的热键组合，请修改后再保存。";
                    return false;
                }
            }
        }

        Draft.WeakReminderLeadMinutes = weakLead;
        Draft.StrongReminderLeadMinutes = strongLead;
        Draft.StrongRepeatIntervalSeconds = repeatInterval;
        Draft.StrongRepeatMaxCount = repeatMax;
        Draft.IdleThresholdMinutes = idleThreshold;
        Draft.PetOpacity = _petOpacity;

        error = null;
        return true;
    }

    private static bool TryParseInt(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>日报输出格式选项。</summary>
/// <param name="Value">设置值。</param>
/// <param name="Text">下拉显示文案。</param>
public sealed record ReportFormatOption(int Value, string Text);

/// <summary>桌宠形象选项。</summary>
/// <param name="Value">设置值。</param>
/// <param name="Text">显示名称。</param>
public sealed record PetSpeciesOption(int Value, string Text);

/// <summary>桌宠皮肤选项（含色块画刷，供设置页预览）。</summary>
public sealed class PetSkinOption
{
    /// <summary>构造皮肤选项。</summary>
    /// <param name="value">设置值。</param>
    /// <param name="text">显示名称。</param>
    /// <param name="hex">色块颜色。</param>
    public PetSkinOption(int value, string text, string hex)
    {
        Value = value;
        Text = text;
        SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Swatch = brush;
    }

    /// <summary>设置值。</summary>
    public int Value { get; }

    /// <summary>显示名称。</summary>
    public string Text { get; }

    /// <summary>色块画刷。</summary>
    public Brush Swatch { get; }
}
