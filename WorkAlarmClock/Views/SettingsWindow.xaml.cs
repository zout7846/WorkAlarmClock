using System.Windows;
using WorkAlarmClock.ViewModels;
using WF = System.Windows.Forms;

namespace WorkAlarmClock.Views;

/// <summary>
/// 设置面板窗口：五区 Tab（作息/提醒/检测/外观/存储），草稿-应用两段式保存。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>构造设置窗口。</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>选择自定义桌宠图片并写入草稿。</summary>
    private void OnChoosePetImage(object sender, RoutedEventArgs e)
    {
        using WF.OpenFileDialog dialog = new WF.OpenFileDialog
        {
            Title = "选择桌宠图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != WF.DialogResult.OK)
        {
            return;
        }

        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.PetImagePath = dialog.FileName;
        }
    }
}
