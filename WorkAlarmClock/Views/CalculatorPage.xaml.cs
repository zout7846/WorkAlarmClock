using System;
using System.Data;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace WorkAlarmClock.Views;
public partial class CalculatorPage : UserControl
{
    public CalculatorPage() { InitializeComponent(); }
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(CalculatorPage), new PropertyMetadata("Expression"));
    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    private static int SelectedRadix(System.Windows.Controls.ComboBox combo) =>
        int.Parse(((System.Windows.Controls.ComboBoxItem)combo.SelectedItem).Tag.ToString()!, CultureInfo.InvariantCulture);

    private void OnConvertRadix(object sender, RoutedEventArgs e)
    {
        string text = Expression.Text.Trim().Replace("_", "");
        try
        {
            if (text.Length == 0 || text.Length > 4096) throw new FormatException();
            int from = SelectedRadix(InputBase);
            int to = SelectedRadix(OutputBase);
            bool negative = text.StartsWith("-", StringComparison.Ordinal);
            string digits = negative ? text.Substring(1) : text;
            if (digits.Length == 0) throw new FormatException();
            BigInteger value = BigInteger.Zero;
            foreach (char ch in digits)
            {
                int digit = ch is >= '0' and <= '9' ? ch - '0' :
                    ch is >= 'a' and <= 'f' ? ch - 'a' + 10 :
                    ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
                if (digit < 0 || digit >= from) throw new FormatException();
                value = value * from + digit;
            }
            if (negative) value = -value;
            RadixResult.Text = $"{from} 进制 → {to} 进制：{FormatRadix(value, to)}";
            History.Text = $"BIN  {FormatRadix(value, 2)}\nOCT  {FormatRadix(value, 8)}\nDEC  {value.ToString(CultureInfo.InvariantCulture)}\nHEX  {FormatRadix(value, 16)}";
        }
        catch { RadixResult.Text = "输入无效：请检查数字是否属于所选进制"; History.Text = string.Empty; }
    }

    private static string FormatRadix(BigInteger value, int radix)
    {
        if (value.IsZero) return "0";
        bool negative = value.Sign < 0;
        BigInteger remaining = BigInteger.Abs(value);
        const string digits = "0123456789ABCDEF";
        var chars = new System.Text.StringBuilder();
        while (remaining > 0)
        {
            remaining = BigInteger.DivRem(remaining, radix, out BigInteger remainder);
            chars.Insert(0, digits[(int)remainder]);
        }
        return negative ? "-" + chars : chars.ToString();
    }

    private void OnKey(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        string input = (button.Tag ?? button.Content).ToString()!;
        int start = Expression.SelectionStart;
        if (input == "-")
        {
            if (start == 0 && !Expression.Text.StartsWith("-", StringComparison.Ordinal)) input = "-";
            else return;
        }
        Expression.SelectedText = input;
        Expression.CaretIndex = start + input.Length;
        Expression.SelectionLength = 0;
        Expression.Focus();
    }
    private void OnCalculate(object sender, RoutedEventArgs e)
    {
        string expression = Expression.Text.Trim();
        if (Mode == "Radix") { OnConvertRadix(sender, e); return; }
        try
        {
            if (expression.Length == 0 || expression.Length > 500 || !Regex.IsMatch(expression, @"^[0-9.()+*/\s-]+$"))
                throw new FormatException();
            using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            double value = Convert.ToDouble(table.Compute(expression, null), CultureInfo.InvariantCulture);
            if (!double.IsFinite(value)) throw new DivideByZeroException();
            string answer = value.ToString("G12", CultureInfo.InvariantCulture);
            Result.Text = "= " + answer;
            History.Text = expression + " = " + answer;
        }
        catch (DivideByZeroException) { Result.Text = "不能除以零"; }
        catch (Exception) { Result.Text = "表达式无效，请检查数字和括号"; }
    }
    private void OnClear(object sender, RoutedEventArgs e) { Expression.Clear(); Result.Text = "= 0"; RadixResult.Text = string.Empty; History.Text = string.Empty; Expression.Focus(); }
    private void OnBackspace(object sender, RoutedEventArgs e)
    {
        if (Expression.SelectionLength > 0) Expression.SelectedText = "";
        else if (Expression.CaretIndex > 0) { int at = Expression.CaretIndex - 1; Expression.Text = Expression.Text.Remove(at, 1); Expression.CaretIndex = at; }
        Expression.Focus();
    }
    private void OnExpressionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnCalculate(sender, e); e.Handled = true; }
        if (e.Key == Key.Escape) { OnClear(sender, e); e.Handled = true; }
    }
}
