using System.Reflection;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using WorkAlarmClock.Views;
using WorkAlarmClock.Services;
using WorkAlarmClock.Models;
using WorkAlarmClock.ViewModels;

class Validation
{
 public sealed class EmptyActions : IHotkeyActions
 {
  public void StartWork(){} public void StartRest(){} public void EndDay(){} public void GenerateReport(){} public void TogglePet(){} public void Screenshot(){} public void FocusToggle(){} public void ToggleVisible(){} public void OpenVariableNameGenerator(){}
 }
 public sealed class SpeedSample : ISpeedTestService
 {
  public bool IsTesting=>true; public SpeedPhase Phase {get;set;}=SpeedPhase.Download; public string NodeName=>"测试节点"; public int LatencyMs=>18; public double DownloadMbps {get;set;}=25; public double UploadMbps {get;set;}=0; public string StageText=>Phase==SpeedPhase.Upload?"正在测试上传":"正在测试下载";
  public event EventHandler ProgressChanged; public event EventHandler Completed {add{} remove{}}
  public Task RunAsync(CancellationToken token=default)=>Task.CompletedTask;
  public void Raise()=>ProgressChanged?.Invoke(this,EventArgs.Empty);
 }
 public sealed class StatsSample : IStatisticsService
 {
  public WeekStats GetStats()=>new(TimeSpan.FromHours(7),TimeSpan.FromHours(1),TimeSpan.FromHours(35),TimeSpan.FromHours(5));
  public IReadOnlyList<DayStat> GetRecentDays(int days)=>Enumerable.Range(0,days).Select(i=>new DayStat(DateOnly.FromDateTime(DateTime.Today).AddDays(i-days+1),TimeSpan.FromHours(i%3+5),TimeSpan.FromMinutes(30+i*5),true)).ToArray();
  public IReadOnlyList<TimelineSlice> GetTodayTimeline()=>new[]{new TimelineSlice(DateTimeOffset.Now.AddHours(-4),DateTimeOffset.Now.AddHours(-1),"work","工作 3 小时"),new TimelineSlice(DateTimeOffset.Now.AddHours(-1),DateTimeOffset.Now,"rest","休息 1 小时")};
 }
 public class CountdownSample { public string TimeText {get;set;}="25:00"; public string StatusText {get;set;}="准备开始"; public string MinutesText {get;set;}="25"; public string ButtonText {get;set;}="开始"; }
 static string Root=Path.GetFullPath("artifacts/ui-checks-"+DateTime.Now.ToString("HHmmss"));
 static int Errors;
 static IEnumerable<T> Desc<T>(DependencyObject o) where T:DependencyObject { if(o is T t) yield return t; for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++) foreach(var c in Desc<T>(VisualTreeHelper.GetChild(o,i))) yield return c; }
 static void Assert(bool ok,string text) { if(!ok) { Errors++; Console.WriteLine("FAIL "+text); } else Console.WriteLine("PASS "+text); }
 static void Layout(FrameworkElement e,int w,int h) { e.Measure(new Size(w,h)); e.Arrange(new Rect(0,0,w,h)); e.UpdateLayout(); }
 static bool Visible(DependencyObject d) { for(var p=d;p is not null;p=VisualTreeHelper.GetParent(p)) if(p is UIElement u && u.Visibility!=Visibility.Visible) return false; return true; }
 static void Render(Window window,int w,int h,string name)
 {
  var root=(FrameworkElement)window.Content; Layout(root,w,h);
  var image=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);
  var background=new DrawingVisual(); using(var dc=background.RenderOpen()) dc.DrawRectangle(window.Background??Brushes.White,null,new Rect(0,0,w,h)); image.Render(background);
  if(root is Panel panel && panel.Background is null) panel.Background=window.Background;
  image.Render(root); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using(var s=File.Create(Path.Combine(Root,name+".png"))) encoder.Save(s);
  foreach(var b in Desc<Button>(root).Where(b=>Visible(b) && b.Content is string))
  {
   string text=(string)b.Content;
   var ft=new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(b.FontFamily,b.FontStyle,b.FontWeight,b.FontStretch),b.FontSize,Brushes.Black,1);
   var cp=Desc<ContentPresenter>(b).FirstOrDefault();
   if(cp!=null && (cp.ActualWidth+1<ft.Width || cp.ActualHeight+1<ft.Height)) { Errors++; Console.WriteLine($"CLIP {name}: {text} content={cp.ActualWidth:0.#}x{cp.ActualHeight:0.#} required={ft.Width:0.#}x{ft.Height:0.#}"); }
  }
  Console.WriteLine("RENDER "+name+" "+w+"x"+h);
  foreach(var tb in Desc<TextBox>(root).Where(t=>Visible(t) && t.Text.Length>0))
  {
   var host=tb.Template.FindName("PART_ContentHost",tb) as FrameworkElement; var text=new FormattedText("测试Ag",System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(tb.FontFamily,tb.FontStyle,tb.FontWeight,tb.FontStretch),tb.FontSize,Brushes.Black,1);
   if(host!=null && host.ActualHeight+1<text.Height) { Errors++; Console.WriteLine($"CLIP INPUT {name}: {tb.Name} {host.ActualHeight:0.#} < {text.Height:0.#}"); }
  }
 }
 static object Call(object o,string method,params object[] args)=>o.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(o,args);
 [STAThread] static int Main()
 {
  Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable("WORKALARM_TEST_ROOT",Path.Combine(Root,"data")); Directory.CreateDirectory(Path.Combine(Root,"data"));
  var app=new Application(); var xml=XDocument.Load("WorkAlarmClock/App.xaml"); var resources=xml.Root.Elements().First(); var dict=new XElement(XName.Get("ResourceDictionary","http://schemas.microsoft.com/winfx/2006/xaml/presentation"),new XAttribute(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml"),resources.Elements()); app.Resources=(ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dict.ToString());
  SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
  using var storage=new StorageService(); var settings=new SettingsService(storage); var services=new ServiceCollection(); services.AddSingleton<ISettingsService>(settings); var speedService=new SpeedSample(); services.AddSingleton<ISpeedTestService>(speedService); services.AddSingleton<IStatisticsService>(new StatsSample()); using var provider=services.BuildServiceProvider(); typeof(AppServices).GetField("_provider",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,provider);
  PageNotifications.Publish("快捷键注册失败","Ctrl + Alt + A 可能已被其他程序占用，请在设置 → 热键中更换。","错误");
  var radix=new CalculatorWindow("Radix"); var page=(CalculatorPage)radix.FindName("Calculator"); ((TextBox)page.FindName("Expression")).Text="255"; Call(page,"OnConvertRadix",page,new RoutedEventArgs()); Assert(((TextBlock)page.FindName("RadixResult")).Text.Contains("FF"),"radix decimal to hex");
  foreach(int w in new[]{372,452,660}) Render(radix,w,480,"radix-"+w);
  var keys=Desc<Button>((DependencyObject)radix.Content).Where(x=>Visible(x) && x.Content is string s && s.Length==1 && "ABCDEF0123456789".Contains(s)).ToArray(); Assert(keys.Length==16,"all sixteen radix keys exist"); foreach(var key in keys) { ((TextBox)page.FindName("Expression")).Clear(); Call(page,"OnKey",key,new RoutedEventArgs()); Assert(((TextBox)page.FindName("Expression")).Text==(string)key.Content,"key "+key.Content); }
  Call(page,"OnClear",page,new RoutedEventArgs()); Assert(((TextBlock)page.FindName("RadixResult")).Text=="" && ((TextBlock)page.FindName("History")).Text=="","radix clear resets results");
  var windows=new Window[]{new CalculatorWindow("Expression"),new CodeToolWindow(),new VariableNameGeneratorWindow(),new ImageConverterWindow(),new WeatherWindow(),new SpeedTestWindow(),new StatisticsWindow(),new CountdownWindow(),new ClipboardHistoryWindow(),new ReportPreviewWindow(),new ReportHistoryWindow(),new SettingsWindow()};
  var speedVM=new SpeedTestViewModel(); for(int i=0;i<110;i++) {speedService.DownloadMbps=20+i%10;speedService.Raise();} speedService.Phase=SpeedPhase.Upload; speedService.UploadMbps=12.5; speedService.Raise(); Assert(speedVM.GaugeLabel=="上传" && speedVM.GaugeValue==12.5,"upload gauge switches to actual upload rate"); Assert(speedVM.DownloadTrend.Count==90 && speedVM.UploadTrend.Count==90 && speedVM.TrendLabels.Count==90,"speed history bounded and aligned");
  var statsVM=new StatisticsViewModel(); Assert(statsVM.WorkTrend.Count==7 && statsVM.TimeShare.Count==2 && Math.Abs(statsVM.TimeShare[0]-statsVM.WorkTrend.Sum())<.001,"statistics charts use same source totals");
  foreach(var win in windows) { if(win is SpeedTestWindow) {win.DataContext=speedVM;Call(win,"BuildGauge");} if(win is StatisticsWindow) win.DataContext=statsVM; if(win is SettingsWindow) win.DataContext=new SettingsViewModel(); if(win is CountdownWindow) win.DataContext=new CountdownSample(); if(win is VariableNameGeneratorWindow v) { ((TextBox)v.FindName("InputText")).Text="userLoginTime"; Call(v,"Generate_Click",v,new RoutedEventArgs()); }
   string name=win.GetType().Name; Render(win,(int)win.Width-32,(int)win.Height-48,name+"-normal"); Render(win,(int)win.MinWidth-32,(int)win.MinHeight-48,name+"-min");
   Assert(Desc<WorkspaceFrame>((DependencyObject)win.Content).Count()==1,name+" navigation");
  }
  var set=new SettingsWindow { DataContext=new SettingsViewModel() }; Layout((FrameworkElement)set.Content,650,510); var tabs=Desc<TabControl>((DependencyObject)set.Content).First();
  for(int i=0;i<tabs.Items.Count;i++) { tabs.SelectedIndex=i; Render(set,650,510,"settings-tab-"+i); }
  var main=new WorkAlarmClock.MainWindow(); main.DataContext=new {NowText="13:30:00",StateText="工作中",WorkDurationText="3 小时 20 分",RestDurationText="25 分",LunchDurationText="1 小时",WeekWorkText="32 小时",FocusTimeText="25:00",FocusButtonText="开始专注",WeatherTileText="上海 18°C",WeatherTileSub="阴 · 今日有雨",CountdownTileText="25:00",ClipboardTileText="12 条记录",NetworkTileText="网络在线"}; var workspace=(TabControl)main.FindName("WorkspaceTabs");
  for(int i=0;i<4;i++) { workspace.SelectedIndex=i; Render(main,852,572,"main-tab-"+i); }
  var trend=new TrendChart {Values=new double[]{0,10,3,15,30,14,20},SecondaryValues=new double[]{2,5,1,7,14,6,9},Labels=new[]{"周一","周二","周三","周四","周五","周六","周日"},Unit="M",Height=200}; var donut=new DonutChart { Values=new double[]{32,8},Labels=new[]{"工作","休息"},Unit="h",Height=170};
  var panel=new StackPanel { Margin=new Thickness(20) }; panel.Children.Add(trend); panel.Children.Add(donut); var charts=new Window { Content=panel,Background=Brushes.White }; Render(charts,520,430,"charts-data"); trend.Values=Array.Empty<double>(); trend.SecondaryValues=Array.Empty<double>(); donut.Values=new double[]{0,0}; Render(charts,380,430,"charts-empty");
  var notices=new NotificationBar(); notices.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); var nwindow=new Window {Content=notices,Background=Brushes.White}; Call(notices,"Next_Click",notices,new RoutedEventArgs()); Render(nwindow,360,220,"notification");
  for(int i=0;i<55;i++) PageNotifications.Publish("测试 "+i,"消息记录 "+i); Assert(PageNotifications.Items.Count==50,"bounded notification history"); PageNotifications.Publish("测试 54","消息记录 54"); Assert(PageNotifications.Items.Count==50,"duplicate notifications merged");
  Assert(((TextBlock)notices.FindName("CurrentText")).Text.Contains("测试 54"),"live notice updates without a modal"); Call(notices,"History_Click",notices,new RoutedEventArgs()); Assert(((ListBox)notices.FindName("HistoryList")).Visibility==Visibility.Visible,"notification history expands inline"); notices.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
  // Invalid keys exercise the same failure branch without registering any global shortcut.
  foreach(var property in settings.Current.Hotkeys.GetType().GetProperties().Where(p=>p.PropertyType==typeof(HotkeyCombo))) property.SetValue(settings.Current.Hotkeys,new HotkeyCombo(0,false,false,false));
  using(var hotkeys=new HotkeyService(settings)) { hotkeys.Install(new EmptyActions()); Assert(PageNotifications.Items[0].Title=="快捷键注册失败","hotkey registration failure returns and publishes inline notice"); }
        foreach (var size in new[] { (800, 200), (200, 800), (777, 333) })
        {
            byte[] pixels = new byte[size.Item1 * size.Item2 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i+1] = 90; pixels[i+2] = 140; pixels[i+3] = 255; }
            var image = BitmapSource.Create(size.Item1, size.Item2, 96, 96, PixelFormats.Bgra32, null, pixels, size.Item1 * 4);
            var window = new ScreenshotAnnotationWindow(image, ".", false);
            var content = (FrameworkElement)window.Content;
            var layer = (Canvas)window.FindName("ShapeLayer");
            var marker = new Rectangle { Width = 20, Height = 20, Fill = Brushes.Red };
            Canvas.SetLeft(marker, 40); Canvas.SetTop(marker, 50); layer.Children.Add(marker);
            foreach (var viewport in new[] { new Size(900, 650), new Size(560, 420) })
            {
                content.Measure(viewport); content.Arrange(new Rect(viewport)); content.UpdateLayout();
                var compose = typeof(ScreenshotAnnotationWindow).GetMethod("Compose", BindingFlags.NonPublic | BindingFlags.Instance);
                var result = (BitmapSource)compose.Invoke(window, null);
                byte[] output = new byte[pixels.Length]; result.CopyPixels(output, size.Item1 * 4, 0);
                int inside = (55 * size.Item1 + 45) * 4;
                int outside = (10 * size.Item1 + 10) * 4;
                if (result.PixelWidth != size.Item1 || result.PixelHeight != size.Item2 || output[inside+2] != 255 || output[inside+1] != 0 || output[outside+2] != 140 || output[outside+1] != 90)
                    throw new Exception("Image or annotation changed after resize");
                Console.WriteLine($"PASS {size} viewport={viewport}: original pixels and annotation position preserved");
            }
            window.Close();
        }
  Console.WriteLine($"RESULT errors={Errors}; artifacts={Root}"); return Errors==0?0:1;
 }
}
