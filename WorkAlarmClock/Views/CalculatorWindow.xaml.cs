using System.Windows;

namespace WorkAlarmClock.Views;

public partial class CalculatorWindow : Window
{
    public CalculatorWindow(string mode)
    {
        InitializeComponent();
        Calculator.Mode = mode;
        if (mode == "Radix") Title = "进制转换";
    }
}
