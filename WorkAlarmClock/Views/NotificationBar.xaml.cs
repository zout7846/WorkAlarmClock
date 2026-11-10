using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WorkAlarmClock.Services;

namespace WorkAlarmClock.Views;

public partial class NotificationBar : UserControl
{
    private int _index;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };
    public NotificationBar()
    {
        InitializeComponent();
        HistoryList.ItemsSource = PageNotifications.Items;
        _timer.Tick += (_, _) => { if (HistoryList.Visibility != Visibility.Visible) { _index++; Refresh(); } };
        Loaded += (_, _) => { PageNotifications.Items.CollectionChanged += Changed; Refresh(); _timer.Start(); };
        Unloaded += (_, _) => { PageNotifications.Items.CollectionChanged -= Changed; _timer.Stop(); };
        Refresh();
    }
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e) { _index = 0; Refresh(); }
    private void Refresh()
    {
        Surface.Visibility = PageNotifications.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (PageNotifications.Items.Count == 0) return;
        _index = (_index + PageNotifications.Items.Count) % PageNotifications.Items.Count;
        var item = PageNotifications.Items[_index];
        CurrentText.Text = item.DisplayText;
        HistoryButton.Content = $"消息记录 ({PageNotifications.Items.Count})";
        Surface.Background = new System.Windows.Media.SolidColorBrush(item.Kind == "错误" ? System.Windows.Media.Color.FromRgb(255, 239, 235) : System.Windows.Media.Color.FromRgb(231, 241, 255));
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { _index--; Refresh(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _index++; Refresh(); }
    private void History_Click(object sender, RoutedEventArgs e) => HistoryList.Visibility = HistoryList.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
