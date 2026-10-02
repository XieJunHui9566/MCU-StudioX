namespace StudioX.Desktop;

using System.Windows.Media;
using StudioX.Application;

internal static class DiagnosticPalette
{
    public static void Apply(ThemeDefinition theme)
    {
        var background = (Color)ColorConverter.ConvertFromString(theme.Colors["Background"]);
        var dark = background.R * .299 + background.G * .587 + background.B * .114 < 140;
        // 诊断色由主题明暗派生，不改变格式 1 主题的八色契约。
        Set("DiagnosticError", dark ? "#FF8894" : "#B42332");
        Set("DiagnosticWarning", dark ? "#F0C36B" : "#825500");
        Set("DiagnosticSuccess", dark ? "#6EBB82" : "#256B3A");
        Set("DiagnosticErrorSurface", dark ? "#3B252B" : "#FDECEF");
        Set("DiagnosticWarningSurface", dark ? "#3A3020" : "#FFF3D6");
    }

    private static void Set(string key, string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        System.Windows.Application.Current.Resources[key] = brush;
    }
}
