using System.Collections.ObjectModel;
using System.Windows;

namespace WorkAlarmClock.Services;

public sealed record PageNotice(DateTime Time, string Title, string Message, string Kind)
{
    public string DisplayText => $"{Time:HH:mm} · {Title}：{Message}";
}

/// <summary>跨页面的非模态消息，保留最近记录，窗口稍后打开也能读取。</summary>
public static class PageNotifications
{
    public static ObservableCollection<PageNotice> Items { get; } = new();
    public static void Publish(string title, string message, string kind = "信息")
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Publish(title, message, kind)));
            return;
        }
        if (Items.Count > 0 && Items[0].Title == title && Items[0].Message == message && DateTime.Now - Items[0].Time < TimeSpan.FromSeconds(5)) return;
        Items.Insert(0, new PageNotice(DateTime.Now, title, message, kind));
        while (Items.Count > 50) Items.RemoveAt(Items.Count - 1);
    }
}
