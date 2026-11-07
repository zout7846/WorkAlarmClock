using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkAlarmClock.Infrastructure;
using WorkAlarmClock.Models;
using WorkAlarmClock.Views;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace WorkAlarmClock.Services;

/// <summary>可截取的窗口（位图像素坐标 + 标题）。</summary>
/// <param name="PixelRect">相对虚拟屏幕左上角的像素矩形。</param>
/// <param name="Title">窗口标题。</param>
public sealed record WindowTarget(Int32Rect PixelRect, string Title);

/// <summary>
/// 截图服务（零第三方依赖）：冻结全屏 → 遮罩选区（可拖拽框选，也可单击窗口）→ 裁剪 →
/// 打开标注窗口（矩形/箭头/画笔）→ 保存 PNG + 可选复制剪贴板。
/// </summary>
public sealed class ScreenshotService : IScreenshotService
{
    private readonly ISettingsService _settings;
    private readonly INotificationService _notification;

    private SD.Bitmap? _frozenBitmap;
    private ScreenshotOverlayWindow? _overlay;

    /// <summary>构造截图服务。</summary>
    /// <param name="settings">设置服务。</param>
    /// <param name="notification">通知服务。</param>
    public ScreenshotService(ISettingsService settings, INotificationService notification)
    {
        _settings = settings;
        _notification = notification;
    }

    /// <inheritdoc />
    public bool IsCapturing => _overlay is not null;

    /// <inheritdoc />
    public void StartCapture()
    {
        if (_overlay is not null)
        {
            _overlay.Activate();
            return;
        }

        try
        {
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(StartCapture);
                return;
            }

            SD.Rectangle bounds = WF.SystemInformation.VirtualScreen;
            _frozenBitmap = CaptureScreen(bounds);

            // 测试钩子：WORKALARM_TEST_ROI="x,y,w,h"（物理像素）时跳过交互遮罩，
            // 直接按该矩形走一遍“裁剪 → 标注窗口”，用于自动化验证比例不失真。
            string? testRoi = Environment.GetEnvironmentVariable("WORKALARM_TEST_ROI");
            if (!string.IsNullOrWhiteSpace(testRoi))
            {
                string[] parts = testRoi.Split(',');
                if (parts.Length == 4
                    && int.TryParse(parts[0], out int rx)
                    && int.TryParse(parts[1], out int ry)
                    && int.TryParse(parts[2], out int rw)
                    && int.TryParse(parts[3], out int rh))
                {
                    OnCaptureCompleted(this, new Int32Rect(rx, ry, rw, rh));
                    return;
                }
            }

            BitmapSource frozen = ToBitmapSource(_frozenBitmap);
            IReadOnlyList<WindowTarget> windows = EnumerateWindows(bounds);

            Rect dipBounds = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            ScreenshotOverlayWindow overlay = new ScreenshotOverlayWindow(frozen, dipBounds, windows);
            _overlay = overlay;
            overlay.CaptureCompleted += OnCaptureCompleted;
            overlay.Closed += OnOverlayClosed;
            overlay.Show();
            overlay.Activate();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("启动截图失败。", ex);
            _notification.ShowBubble("截图启动失败：" + ex.Message, BubbleKind.Info);
            ReleaseFrozen();
        }
    }

    /// <inheritdoc />
    public void CloseOverlay()
    {
        try
        {
            _overlay?.Close();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("关闭截图遮罩失败。", ex);
        }
        finally
        {
            _overlay = null;
            ReleaseFrozen();
        }
    }

    /// <inheritdoc />
    public string ResolveScreenshotDirectory()
    {
        string? testDirectory = Environment.GetEnvironmentVariable("WORKALARM_TEST_SCREENSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(testDirectory))
        {
            return testDirectory;
        }

        string configured = _settings.Current.ScreenshotDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? AppEnvironment.DefaultScreenshotDirectory
            : configured;
    }

    /// <summary>遮罩关闭：释放冻结位图。</summary>
    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (_overlay is not null)
        {
            _overlay.CaptureCompleted -= OnCaptureCompleted;
            _overlay.Closed -= OnOverlayClosed;
        }

        _overlay = null;
        ReleaseFrozen();
    }

    /// <summary>选区完成：裁剪 → 打开标注窗口。</summary>
    private void OnCaptureCompleted(object? sender, Int32Rect rect)
    {
        try
        {
            if (_frozenBitmap is null)
            {
                return;
            }

            // 窗口可能部分在屏幕外或超出虚拟屏，裁剪前先夹紧到冻结位图范围内。
            int x = Math.Clamp(rect.X, 0, Math.Max(0, _frozenBitmap.Width - 1));
            int y = Math.Clamp(rect.Y, 0, Math.Max(0, _frozenBitmap.Height - 1));
            int width = Math.Min(rect.Width, _frozenBitmap.Width - x);
            int height = Math.Min(rect.Height, _frozenBitmap.Height - y);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            using SD.Bitmap cropped = _frozenBitmap.Clone(
                new SD.Rectangle(x, y, width, height),
                _frozenBitmap.PixelFormat);

            BitmapSource source = ToBitmapSource(cropped);
            OpenAnnotation(source);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("截图裁剪失败。", ex);
            _notification.ShowBubble("截图失败：" + ex.Message, BubbleKind.Info);
        }
        finally
        {
            ReleaseFrozen();
        }
    }

    /// <summary>打开标注窗口，保存成功后给出提示。</summary>
    private void OpenAnnotation(BitmapSource image)
    {
        ScreenshotAnnotationWindow window = new ScreenshotAnnotationWindow(
            image,
            ResolveScreenshotDirectory(),
            _settings.Current.ScreenshotCopyToClipboard);

        window.Saved += (_, path) =>
        {
            AppEnvironment.Log("截图已保存：" + path);
            _notification.ShowBubble(
                "截图已保存：" + Path.GetFileName(path),
                BubbleKind.Info,
                "打开目录",
                () => OpenFolder(Path.GetDirectoryName(path) ?? ResolveScreenshotDirectory()));
            _notification.ShowTrayBalloon("截图已保存", Path.GetFileName(path));
        };

        window.Show();
        window.Activate();
    }

    /// <summary>枚举可截取的顶层窗口（可见、未最小化、有标题、非本进程、尺寸合理）。</summary>
    private static IReadOnlyList<WindowTarget> EnumerateWindows(SD.Rectangle virtualScreen)
    {
        List<WindowTarget> targets = new List<WindowTarget>();
        uint self = (uint)Environment.ProcessId;

        try
        {
            NativeMethods.EnumWindows((handle, _) =>
            {
                if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
                {
                    return true;
                }

                if (NativeMethods.GetWindowTextLength(handle) == 0)
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
                if (pid == self)
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect))
                {
                    return true;
                }

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width < 120 || height < 80)
                {
                    return true;
                }

                // 夹紧到虚拟屏幕范围内（窗口可能部分在屏幕外）。
                int left = Math.Max(0, Math.Min(rect.Left - virtualScreen.Left, virtualScreen.Width - 1));
                int top = Math.Max(0, Math.Min(rect.Top - virtualScreen.Top, virtualScreen.Height - 1));
                int clampedWidth = Math.Min(width, virtualScreen.Width - left);
                int clampedHeight = Math.Min(height, virtualScreen.Height - top);
                if (clampedWidth < 120 || clampedHeight < 80)
                {
                    return true;
                }

                StringBuilder title = new StringBuilder(256);
                NativeMethods.GetWindowText(handle, title, title.Capacity);
                if (title.Length == 0)
                {
                    return true;
                }

                targets.Add(new WindowTarget(
                    new Int32Rect(left, top, clampedWidth, clampedHeight),
                    title.ToString()));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("枚举窗口失败（窗口截图不可用）。", ex);
        }

        return targets;
    }

    /// <summary>按虚拟屏幕范围抓取一张位图（含多显示器）。</summary>
    private static SD.Bitmap CaptureScreen(SD.Rectangle bounds)
    {
        SD.Bitmap bitmap = new SD.Bitmap(
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height),
            SD.Imaging.PixelFormat.Format32bppArgb);

        using SD.Graphics graphics = SD.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size, SD.CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>位图 → WPF 图像源（复制像素，不共享 GDI 句柄）。</summary>
    private static BitmapSource ToBitmapSource(SD.Bitmap bitmap)
    {
        SD.Rectangle rect = new SD.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        SD.Imaging.BitmapData data = bitmap.LockBits(rect, SD.Imaging.ImageLockMode.ReadOnly, SD.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            BitmapSource source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                data.Scan0,
                data.Stride * bitmap.Height,
                data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>复制到剪贴板（剪贴板偶发被占用，做有限重试）。</summary>
    internal static bool TryCopyToClipboard(BitmapSource image)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(image);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == 4)
                {
                    AppEnvironment.Log("复制截图到剪贴板失败。", ex);
                    return false;
                }

                Thread.Sleep(60);
            }
        }

        return false;
    }

    /// <summary>生成不覆盖既有文件的截图路径：2026.11.04_153012.png（重名追加 _2、_3）。</summary>
    internal static string BuildFilePath(string directory)
    {
        DateTime now = DateTime.Now;
        string stamp = now.ToString("yyyy.MM.dd_HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, stamp + ".png");
        int index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, stamp + "_" + index.ToString(CultureInfo.InvariantCulture) + ".png");
            index++;
        }

        return path;
    }

    private static void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
            process?.Dispose();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("打开截图目录失败。", ex);
        }
    }

    private void ReleaseFrozen()
    {
        try
        {
            _frozenBitmap?.Dispose();
        }
        catch (Exception ex)
        {
            AppEnvironment.Log("释放截屏位图失败。", ex);
        }
        finally
        {
            _frozenBitmap = null;
        }
    }
}
