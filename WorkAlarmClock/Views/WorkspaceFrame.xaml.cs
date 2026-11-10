using System.Windows;
using System.Windows.Controls;

namespace WorkAlarmClock.Views;

public partial class WorkspaceFrame : UserControl
{
    public WorkspaceFrame()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (Window.GetWindow(this) is {} window) FitToWorkArea(window); };
    }
    internal static void FitToWorkArea(Window window)
    {
        // Logical pixels: high-DPI screens can have a smaller available work area.
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        if (window.Width > area.Width) window.Width = area.Width;
        if (window.Height > area.Height) window.Height = area.Height;
    }
    private void Home_Click(object sender, RoutedEventArgs e) => App.ShowMainPanel();
    private void Tools_Click(object sender, RoutedEventArgs e) => App.ShowToolsPage();
    private void Settings_Click(object sender, RoutedEventArgs e) => App.ShowSettingsPage();
}
