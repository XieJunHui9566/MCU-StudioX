namespace StudioX.Desktop;

using System.Globalization;
using System.Windows.Data;
using StudioX.Engine;

public sealed class Ag32PinSummaryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Ag32PinAssignment pin)
        {
            return "";
        }
        if (!pin.Function.StartsWith("GPIO", StringComparison.Ordinal))
        {
            return "外设复用";
        }
        var mode = pin.Direction switch
        {
            "OUTPUT" => pin.InitialHigh ? "输出高" : "输出低",
            "INPUT" => "输入",
            _ => "方向由应用设置"
        };
        var pull = pin.Pull switch
        {
            "UP" => "上拉",
            "DOWN" => "下拉",
            "HOLD" => "总线保持",
            _ => "浮空"
        };
        return mode + " · " + pull + (pin.Direction == "OUTPUT" ? pin.OutputType == "OPEN_DRAIN" ? " · 开漏" : " · 推挽" : "") +
            (pin.Interrupt == "NONE" ? "" : " · 中断");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
