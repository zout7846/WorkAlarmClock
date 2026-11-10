using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WorkAlarmClock.Views;

public partial class VariableNameGeneratorWindow : Window
{
    private bool _busy;
    private string? _translatedInput;

    public VariableNameGeneratorWindow() { InitializeComponent(); InputText.Focus(); }

    private async void TranslateToEnglish_Click(object sender, RoutedEventArgs e) => await TranslateAsync("en");
    private async void TranslateToChinese_Click(object sender, RoutedEventArgs e) => await TranslateAsync("zh-CN");

    private async Task TranslateAsync(string target)
    {
        if (_busy) return;
        string input = InputText.Text.Trim();
        if (input.Length == 0) { StatusText.Text = "请先输入需要翻译的内容。"; return; }
        _busy = true; Actions.IsEnabled = false;
        TranslationText.Clear(); NamesList.ItemsSource = null; _translatedInput = null;
        StatusText.Text = "正在翻译…";
        try
        {
            string query = target == "zh-CN" ? SplitWords(input) : input;
            var result = await WorkAlarmClock.Services.ToolTranslationService.TranslateAsync(query, target);
            if (InputText.Text.Trim() != input) { StatusText.Text = "输入已更改，请重新翻译。"; return; }
            TranslationText.Text = result.Text;
            _translatedInput = target == "en" ? input : null;
            if (target == "en") SetNames(result.Text);
            else NamesList.ItemsSource = null;
            StatusText.Text = "翻译完成 · " + result.Provider;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _busy = false; Actions.IsEnabled = true; }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (ContainsHan(InputText.Text) && _translatedInput != InputText.Text.Trim()) { await TranslateAsync("en"); return; }
        SetNames(_translatedInput == InputText.Text.Trim() ? TranslationText.Text : InputText.Text);
    }

    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (NamesList is null) return;
        _translatedInput = null; TranslationText.Clear(); NamesList.ItemsSource = null;
    }
    private void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranslationText.Text)) { StatusText.Text = "还没有译文可复制。"; return; }
        try { Clipboard.SetText(TranslationText.Text); StatusText.Text = "译文已复制。"; }
        catch { StatusText.Text = "剪贴板被占用，请重试。"; }
    }
    private static string SplitWords(string text) => Regex.Replace(Regex.Replace(text, "([A-Z]+)([A-Z][a-z])", "$1 $2"), "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ').Replace('-', ' ');

    private void SetNames(string text)
    {
        string[] words = Regex.Matches(SplitWords(text), "[A-Za-z0-9]+")
            .Select(match => match.Value.ToLowerInvariant()).ToArray();
        if (words.Length == 0) { NamesList.ItemsSource = null; StatusText.Text = "没有可用于变量名的英文或数字内容。"; return; }
        if (char.IsDigit(words[0][0])) words[0] = "value" + words[0];
        string pascal = string.Concat(words.Select(Capitalize));
        string camel = words[0] + string.Concat(words.Skip(1).Select(Capitalize));
        NamesList.ItemsSource = new[]
        {
            new NameItem("小驼峰命名", camel), new NameItem("大驼峰命名", pascal),
            new NameItem("下划线命名", string.Join('_', words)), new NameItem("短横线命名", string.Join('-', words)),
            new NameItem("大写下划线", string.Join('_', words).ToUpperInvariant())
        };
        StatusText.Text = "已生成 5 种命名格式。";
    }

    private static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
    private static bool ContainsHan(string value) => value.Any(c => c >= '\u3400' && c <= '\u9fff');
    private void Paste_Click(object sender, RoutedEventArgs e) { try { if (Clipboard.ContainsText()) InputText.Text = Clipboard.GetText(); } catch { StatusText.Text = "无法读取剪贴板。"; } }
    private void CopyOne_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText((string)((Button)sender).Tag); StatusText.Text = "已复制变量名。"; } catch { StatusText.Text = "复制失败。"; } }
    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        string text = string.Join(Environment.NewLine, NamesList.Items.Cast<NameItem>().Select(item => $"{item.Format}\t{item.Value}"));
        if (text.Length == 0) { StatusText.Text = "请先生成变量名。"; return; }
        try { Clipboard.SetText(text); StatusText.Text = "所有命名格式已复制。"; } catch { StatusText.Text = "复制失败。"; }
    }
    private sealed record NameItem(string Format, string Value);
}
