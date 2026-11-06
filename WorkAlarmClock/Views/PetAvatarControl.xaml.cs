using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WorkAlarmClock.Models;

namespace WorkAlarmClock.Views;

/// <summary>
/// 桌宠主体控件：显示 AI 生成的透明背景立绘（Assets/pet.png）。
/// 状态/提醒等外部依赖属性保留用于绑定兼容，但视觉主体改为图片，仅保留呼吸与互动动画。
/// </summary>
public partial class PetAvatarControl : System.Windows.Controls.UserControl
{
    /// <summary>当前主状态（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty CurrentStateProperty = DependencyProperty.Register(
        nameof(CurrentState), typeof(WorkState), typeof(PetAvatarControl),
        new PropertyMetadata(WorkState.BeforeWork));

    /// <summary>当前休息来源（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty RestSourceValueProperty = DependencyProperty.Register(
        nameof(RestSourceValue), typeof(RestSource?), typeof(PetAvatarControl),
        new PropertyMetadata(null));

    /// <summary>提醒叠加（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty ReminderOverlayProperty = DependencyProperty.Register(
        nameof(ReminderOverlay), typeof(ReminderKind?), typeof(PetAvatarControl),
        new PropertyMetadata(null));

    /// <summary>毛色渐变（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty FurGradientProperty = DependencyProperty.Register(
        nameof(FurGradient), typeof(Brush), typeof(PetAvatarControl),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>纯色毛色（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty FurSolidProperty = DependencyProperty.Register(
        nameof(FurSolid), typeof(Brush), typeof(PetAvatarControl),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>浅色毛（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty FurLightProperty = DependencyProperty.Register(
        nameof(FurLight), typeof(Brush), typeof(PetAvatarControl),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>描边色（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty FurOutlineProperty = DependencyProperty.Register(
        nameof(FurOutline), typeof(Brush), typeof(PetAvatarControl),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>耳内粉色（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty EarInnerProperty = DependencyProperty.Register(
        nameof(EarInner), typeof(Brush), typeof(PetAvatarControl),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>腮红色（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty BlushProperty = DependencyProperty.Register(
        nameof(Blush), typeof(Brush), typeof(PetAvatarControl),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>鼻子颜色（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty NoseColorProperty = DependencyProperty.Register(
        nameof(NoseColor), typeof(Brush), typeof(PetAvatarControl),
        new PropertyMetadata(Brushes.Transparent));

    /// <summary>形象（保留用于绑定兼容）。</summary>
    public static readonly DependencyProperty SpeciesProperty = DependencyProperty.Register(
        nameof(Species), typeof(int), typeof(PetAvatarControl),
        new PropertyMetadata(0));

    /// <summary>构造桌宠控件。</summary>
    public PetAvatarControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>当前主状态。</summary>
    public WorkState CurrentState
    {
        get => (WorkState)GetValue(CurrentStateProperty);
        set => SetValue(CurrentStateProperty, value);
    }

    /// <summary>当前休息来源。</summary>
    public RestSource? RestSourceValue
    {
        get => (RestSource?)GetValue(RestSourceValueProperty);
        set => SetValue(RestSourceValueProperty, value);
    }

    /// <summary>提醒叠加。</summary>
    public ReminderKind? ReminderOverlay
    {
        get => (ReminderKind?)GetValue(ReminderOverlayProperty);
        set => SetValue(ReminderOverlayProperty, value);
    }

    /// <summary>毛色渐变。</summary>
    public Brush FurGradient
    {
        get => (Brush)GetValue(FurGradientProperty);
        set => SetValue(FurGradientProperty, value);
    }

    /// <summary>纯色毛色。</summary>
    public Brush FurSolid
    {
        get => (Brush)GetValue(FurSolidProperty);
        set => SetValue(FurSolidProperty, value);
    }

    /// <summary>浅色毛。</summary>
    public Brush FurLight
    {
        get => (Brush)GetValue(FurLightProperty);
        set => SetValue(FurLightProperty, value);
    }

    /// <summary>描边色。</summary>
    public Brush FurOutline
    {
        get => (Brush)GetValue(FurOutlineProperty);
        set => SetValue(FurOutlineProperty, value);
    }

    /// <summary>耳内粉色。</summary>
    public Brush EarInner
    {
        get => (Brush)GetValue(EarInnerProperty);
        set => SetValue(EarInnerProperty, value);
    }

    /// <summary>腮红色。</summary>
    public Brush Blush
    {
        get => (Brush)GetValue(BlushProperty);
        set => SetValue(BlushProperty, value);
    }

    /// <summary>鼻子颜色。</summary>
    public Brush NoseColor
    {
        get => (Brush)GetValue(NoseColorProperty);
        set => SetValue(NoseColorProperty, value);
    }

    /// <summary>形象。</summary>
    public int Species
    {
        get => (int)GetValue(SpeciesProperty);
        set => SetValue(SpeciesProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => StartBreathing();

    /// <summary>
    /// 设置桌宠图片：优先用户自定义图片，回退到内置 pet.png。
    /// </summary>
    /// <param name="customPath">用户自定义图片路径（空或不存在时用内置素材）。</param>
    public void SetPetImage(string? customPath)
    {
        try
        {
            PetImage.Source = LoadImage(customPath);
        }
        catch (Exception)
        {
            // 图片加载失败时保持上一次的图像。
        }
    }

    /// <summary>加载自定义图片，失败回退内置素材。</summary>
    private static ImageSource LoadImage(string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && System.IO.File.Exists(customPath))
        {
            try
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(customPath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // 自定义图片损坏时回退默认。
            }
        }

        return new BitmapImage(new Uri("pack://application:,,,/Assets/pet.png"));
    }

    /// <summary>呼吸起伏：3 秒一个周期，上下缩放至 1.02 倍。</summary>
    private void StartBreathing()
    {
        try
        {
            SineEase ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            TimeSpan halfCycle = TimeSpan.FromMilliseconds(1500);

            BreathScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, 1.02, halfCycle)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease
                });
            BreathScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.0, 1.006, halfCycle)
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = ease
                });
        }
        catch (Exception)
        {
            // 呼吸动画失败不影响显示。
        }
    }

    /// <summary>单击互动：轻微弹跳（叠加在呼吸动画上）。</summary>
    public void PlayInteraction()
    {
        try
        {
            JumpTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimationUsingKeyFrames
                {
                    KeyFrames =
                    {
                        new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                        new EasingDoubleKeyFrame(-12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))),
                        new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320))),
                        new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))),
                        new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)))
                    }
                });
        }
        catch (Exception)
        {
            // 互动动画失败不影响可用性。
        }
    }
}
