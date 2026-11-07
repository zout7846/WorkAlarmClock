using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using WorkAlarmClock.Infrastructure;

namespace WorkAlarmClock.Services;

/// <summary>灵动岛展示的活动类型（同时代表显示优先级，数值越小越优先）。</summary>
public enum SystemActivityKind
{
    /// <summary>摄像头占用。</summary>
    Camera = 0,

    /// <summary>麦克风占用。</summary>
    Microphone = 1,

    /// <summary>正在播放音频。</summary>
    Media = 2,

    /// <summary>本应用正在区域截图。</summary>
    Capturing = 3
}

/// <summary>一条系统活动：类型 + 触发该活动的程序展示名（可能为空字符串）。</summary>
public sealed record SystemActivity(SystemActivityKind Kind, string Program);

/// <summary>
/// 系统状态监控：摄像头 / 麦克风占用（注册表 ConsentStore）、是否有音频在播放（默认输出端点峰值 + 会话进程）、
/// 以及本应用是否正在截图。供灵动岛做「活动状态 + 程序」提示。
/// </summary>
public sealed class SystemStatusService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>本应用在灵动岛上的展示名（截图活动）。</summary>
    private const string SelfProgramName = "打卡桌宠";

    /// <summary>常见程序名别名（键为不带扩展名的进程名或包名，值为中文展示名）。</summary>
    private static readonly Dictionary<string, string> AppNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // 即时通讯 / 会议
        ["WeChat"] = "微信",
        ["Weixin"] = "微信",
        ["WeChatAppEx"] = "微信",
        ["QQ"] = "QQ",
        ["QQNT"] = "QQ",
        ["TIM"] = "TIM",
        ["DingTalk"] = "钉钉",
        ["Feishu"] = "飞书",
        ["Lark"] = "飞书",
        ["ms-teams"] = "Teams",
        ["msteams"] = "Teams",
        ["Teams"] = "Teams",
        ["Microsoft.Teams"] = "Teams",
        ["microsoftteams"] = "Teams",
        ["Zoom"] = "Zoom",
        ["Discord"] = "Discord",
        ["Telegram"] = "Telegram",
        ["Skype"] = "Skype",
        ["Microsoft.SkypeApp"] = "Skype",
        // 浏览器
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["firefox"] = "Firefox",
        // 音视频 / 直播
        ["Spotify"] = "Spotify",
        ["SpotifyAB.SpotifyMusic"] = "Spotify",
        ["cloudmusic"] = "网易云音乐",
        ["QQMusic"] = "QQ音乐",
        ["Kugou"] = "酷狗音乐",
        ["KuGou"] = "酷狗音乐",
        ["vlc"] = "VLC",
        ["PotPlayerMini64"] = "PotPlayer",
        ["potplayermini64"] = "PotPlayer",
        ["obs64"] = "OBS",
        ["obs32"] = "OBS",
        ["Microsoft.ZuneMusic"] = "媒体播放器",
        ["Microsoft.Media.Player"] = "媒体播放器",
        ["4DF9E0F8.Netflix"] = "Netflix",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        // 系统自带（摄像头 / 麦克风最常见占用者）
        ["Microsoft.WindowsCamera"] = "相机",
        ["Microsoft.WindowsSoundRecorder"] = "录音机",
        ["Microsoft.ScreenSketch"] = "截图工具",
        ["Microsoft.Windows.Photos"] = "照片",
        ["Microsoft.Paint"] = "画图",
        ["Microsoft.WindowsNotepad"] = "记事本",
        ["Microsoft.YourPhone"] = "手机连接",
        ["Microsoft.Todos"] = "To Do"
    };

    private readonly DispatcherTimer _timer;
    private bool _camera;
    private bool _mic;
    private bool _media;
    private bool _capturing;
    private string _signature = string.Empty;
    private IReadOnlyList<SystemActivity> _activities = Array.Empty<SystemActivity>();

    /// <summary>构造并启动轮询。</summary>
    public SystemStatusService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    /// <summary>状态变化事件（UI 线程）。</summary>
    public event EventHandler? StatusChanged;

    /// <summary>摄像头是否正在使用。</summary>
    public bool CameraInUse => _camera;

    /// <summary>麦克风是否正在使用。</summary>
    public bool MicInUse => _mic;

    /// <summary>是否有音频在播放。</summary>
    public bool MediaPlaying => _media;

    /// <summary>是否正在截图。</summary>
    public bool Capturing => _capturing;

    /// <summary>当前活动列表（已按 摄像头 &gt; 麦克风 &gt; 音频 &gt; 截图 排序）。</summary>
    public IReadOnlyList<SystemActivity> Activities => _activities;

    private void Poll()
    {
        IReadOnlyList<SystemActivity> activities = CollectActivities();
        string signature = BuildSignature(activities);
        if (string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            return;
        }

        _signature = signature;
        _activities = activities;
        _camera = Contains(activities, SystemActivityKind.Camera);
        _mic = Contains(activities, SystemActivityKind.Microphone);
        _media = Contains(activities, SystemActivityKind.Media);
        _capturing = Contains(activities, SystemActivityKind.Capturing);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>汇总当前所有系统活动（测试钩子优先）。</summary>
    private static List<SystemActivity> CollectActivities()
    {
        string? overridePath = Environment.GetEnvironmentVariable("WORKALARM_TEST_ACTIVITY_FILE");
        if (!string.IsNullOrEmpty(overridePath))
        {
            return ReadActivitiesFromFile(overridePath);
        }

        var activities = new List<SystemActivity>(4);
        if (TryResolve(GetCameraProgram) is { } cameraProgram)
        {
            activities.Add(new SystemActivity(SystemActivityKind.Camera, cameraProgram));
        }

        if (TryResolve(GetMicrophoneProgram) is { } microphoneProgram)
        {
            activities.Add(new SystemActivity(SystemActivityKind.Microphone, microphoneProgram));
        }

        if (TryResolve(GetMediaProgram) is { } mediaProgram)
        {
            activities.Add(new SystemActivity(SystemActivityKind.Media, mediaProgram));
        }

        bool capturing;
        try
        {
            capturing = AppServices.Screenshot.IsCapturing;
        }
        catch (Exception)
        {
            capturing = false;
        }

        if (capturing)
        {
            activities.Add(new SystemActivity(SystemActivityKind.Capturing, SelfProgramName));
        }

        return activities;
    }

    private static string BuildSignature(IReadOnlyList<SystemActivity> activities)
    {
        if (activities.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(activities.Count * 16);
        foreach (SystemActivity activity in activities)
        {
            builder.Append((int)activity.Kind).Append('=').Append(activity.Program).Append('|');
        }

        return builder.ToString();
    }

    private static bool Contains(IReadOnlyList<SystemActivity> activities, SystemActivityKind kind)
    {
        for (int i = 0; i < activities.Count; i++)
        {
            if (activities[i].Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryResolve(Func<string?> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>测试钩子：从文件读取伪造活动（每行 <c>Kind=程序名</c>），不读取文件即视为无活动。</summary>
    private static List<SystemActivity> ReadActivitiesFromFile(string path)
    {
        var activities = new List<SystemActivity>(4);
        try
        {
            if (!File.Exists(path))
            {
                return activities;
            }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                string kindText = separator < 0 ? line : line[..separator];
                string program = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
                if (!Enum.TryParse(kindText, true, out SystemActivityKind kind))
                {
                    continue;
                }

                if (activities.Any(a => a.Kind == kind))
                {
                    continue;
                }

                activities.Add(new SystemActivity(kind, program));
            }
        }
        catch (Exception)
        {
            // 测试钩子失败时按无活动处理。
        }

        activities.Sort((left, right) => left.Kind.CompareTo(right.Kind));
        return activities;
    }

    /// <summary>摄像头占用程序名；未占用返回 null。</summary>
    private static string? GetCameraProgram() => FindDeviceProgram("webcam");

    /// <summary>麦克风占用程序名；未占用返回 null。</summary>
    private static string? GetMicrophoneProgram() => FindDeviceProgram("microphone");

    /// <summary>读取 Windows 隐私「正在使用」记录，返回最近开始占用的程序名。</summary>
    private static string? FindDeviceProgram(string category)
    {
        string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + category;
        string? currentUser = FindActiveApp(Registry.CurrentUser, path, out long currentUserStart);
        string? localMachine = FindActiveApp(Registry.LocalMachine, path, out long localMachineStart);
        if (currentUser is null)
        {
            return localMachine;
        }

        if (localMachine is null)
        {
            return currentUser;
        }

        return localMachineStart > currentUserStart ? localMachine : currentUser;
    }

    private static string? FindActiveApp(RegistryKey hive, string path, out long start)
    {
        start = 0;
        using RegistryKey? key = hive.OpenSubKey(path);
        if (key is null)
        {
            return null;
        }

        string? best = null;
        foreach (string name in key.GetSubKeyNames())
        {
            using RegistryKey? child = key.OpenSubKey(name);
            if (child is null)
            {
                continue;
            }

            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string app in child.GetSubKeyNames())
                {
                    using RegistryKey? appKey = child.OpenSubKey(app);
                    if (appKey is null || !TryReadActiveTime(appKey, out long appStart))
                    {
                        continue;
                    }

                    if (best is null || appStart > start)
                    {
                        best = FriendlyProgramName(Path.GetFileName(app.Replace('#', '\\')));
                        start = appStart;
                    }
                }

                continue;
            }

            if (TryReadActiveTime(child, out long childStart) && (best is null || childStart > start))
            {
                best = FriendlyProgramName(name);
                start = childStart;
            }
        }

        return best;
    }

    private static bool TryReadActiveTime(RegistryKey key, out long start)
    {
        start = 0;
        object? startValue = key.GetValue("LastUsedTimeStart");
        object? stopValue = key.GetValue("LastUsedTimeStop");
        if (startValue is null || stopValue is null)
        {
            return false;
        }

        try
        {
            start = Convert.ToInt64(startValue, CultureInfo.InvariantCulture);
            long stop = Convert.ToInt64(stopValue, CultureInfo.InvariantCulture);
            return start > 0 && stop == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>播放音频的程序名；没有音频在播放返回 null，播放中但识别不到进程返回空字符串。</summary>
    private static string? GetMediaProgram()
    {
        if (!IsAudioPlaying())
        {
            return null;
        }

        string? program = null;
        try
        {
            program = GetPlayingSessionProgram();
        }
        catch (Exception)
        {
            program = null;
        }

        return program ?? string.Empty;
    }

    /// <summary>枚举默认输出端点的音频会话，找出处于 Active 且非系统声音的最低序号会话所属进程。</summary>
    private static string? GetPlayingSessionProgram()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out device) != 0 || device is null)
            {
                return null;
            }

            Guid iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            if (device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object? activated) != 0
                || activated is not IAudioSessionManager2 sessionManager)
            {
                return null;
            }

            manager = sessionManager;
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator? enumeratorSessions) != 0 || enumeratorSessions is null)
            {
                return null;
            }

            sessions = enumeratorSessions;
            if (sessions.GetCount(out int count) != 0)
            {
                return null;
            }

            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out object? raw) != 0 || raw is null)
                {
                    continue;
                }

                try
                {
                    if (raw is not IAudioSessionControl2 control)
                    {
                        continue;
                    }

                    if (control.GetState(out int state) != 0 || state != 1 /*AudioSessionStateActive*/)
                    {
                        continue;
                    }

                    // IsSystemSoundsSession 返回 S_OK(0) 表示这是系统提示音会话。
                    if (control.IsSystemSoundsSession() == 0)
                    {
                        continue;
                    }

                    if (control.GetProcessId(out uint processId) != 0 || processId == 0)
                    {
                        continue;
                    }

                    return ProgramNameFromProcessId(processId);
                }
                finally
                {
                    Marshal.ReleaseComObject(raw);
                }
            }

            return null;
        }
        finally
        {
            if (sessions is not null)
            {
                Marshal.ReleaseComObject(sessions);
            }

            if (manager is not null)
            {
                Marshal.ReleaseComObject(manager);
            }

            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static string? ProgramNameFromProcessId(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return FriendlyProgramName(process.ProcessName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>把进程名 / 包名 / 带 # 分隔的路径统一转成中文展示名。</summary>
    private static string FriendlyProgramName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string name = raw.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (AppNameAliases.TryGetValue(name, out string? alias))
        {
            return alias;
        }

        // 打包应用：Microsoft.WindowsCamera_8wekyb3d8bbwe → 取包名前缀映射，未收录则退化成最后一段。
        int underscore = name.IndexOf('_');
        if (underscore > 0)
        {
            string packageName = name[..underscore];
            if (AppNameAliases.TryGetValue(packageName, out alias))
            {
                return alias;
            }

            int dot = packageName.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < packageName.Length)
            {
                return packageName[(dot + 1)..];
            }
        }

        return name;
    }

    /// <summary>默认扬声器峰值 &gt; 阈值即认为有音频在播放（音乐/视频/通话都覆盖）。</summary>
    private static bool IsAudioPlaying()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioMeterInformation? meter = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out device);
            if (hr != 0 || device is null)
            {
                return false;
            }

            Guid iid = typeof(IAudioMeterInformation).GUID;
            hr = device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object? obj);
            if (hr != 0 || obj is not IAudioMeterInformation info)
            {
                return false;
            }

            meter = info;
            meter.GetPeakValue(out float peak);
            return peak > 0.0025f;
        }
        finally
        {
            if (meter is not null)
            {
                Marshal.ReleaseComObject(meter);
            }

            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Stop();

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);

        int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags, out IntPtr audioVolume);

        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);

        int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);

        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        int GetGroupingParam(out Guid groupingParam);

        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        int RegisterAudioSessionNotification(IntPtr newNotifications);

        int UnregisterAudioSessionNotification(IntPtr newNotifications);

        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);

        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);

        int GetProcessId(out uint processId);

        int IsSystemSoundsSession();

        int SetDuckingPreference(bool optOut);
    }
}
