namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Media;

/// <summary>文件类型同时用颜色与字形区分，在窄目录树和编辑标签中保持可辨认。</summary>
public sealed class FileIcon : FrameworkElement
{
    public string FileName { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsExpanded { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.PushTransform(new ScaleTransform(ActualWidth / 20, ActualHeight / 20));
        if (IsDirectory)
        {
            var brush = Brush("#E0B55E");
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(42, brush.Color.R, brush.Color.G, brush.Color.B)), new Pen(brush, 1.2),
                Geometry.Parse(IsExpanded ? "M2,6 L2,4 L8,4 L10,6 L17,6 L17,8 M2,8 L19,8 L16,16 L2,16 Z" : "M2,6 L2,4 L8,4 L10,6 L18,6 L18,16 L2,16 Z"));
        }
        else if (CodeLanguage.ForFile(FileName) == "CMake")
        {
            dc.DrawGeometry(Brush("#5D9FFF"), null, Geometry.Parse("M10,2 L2,17 L10,12 Z"));
            dc.DrawGeometry(Brush("#4AC79B"), null, Geometry.Parse("M10,2 L18,17 L10,12 Z"));
            dc.DrawGeometry(Brush("#F16E75"), null, Geometry.Parse("M3,18 L10,13 L17,18 Z"));
        }
        else
        {
            var extension = System.IO.Path.GetExtension(FileName).ToLowerInvariant();
            var (label, color) = extension switch
            {
                ".c" => ("C", "#5D9FFF"), ".h" => ("H", "#BB83F4"),
                ".cpp" or ".cc" or ".cxx" => ("C+", "#5D9FFF"),
                ".hpp" or ".hh" or ".hxx" => ("H+", "#BB83F4"),
                ".s" or ".asm" => ("S", "#F0B957"), ".ld" or ".lds" => ("LD", "#EB9852"),
                ".v" or ".sv" => ("V", "#78C8B9"), ".ve" => ("VE", "#78C8B9"),
                ".json" => ("{}", "#E4B957"), ".xml" or ".svd" => ("<>", "#53C59A"),
                ".md" or ".txt" => ("T", "#A5B5CB"), _ => ("·", "#A5B5CB")
            };
            var brush = Brush(color);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(30, brush.Color.R, brush.Color.G, brush.Color.B)), new Pen(brush, 1),
                Geometry.Parse("M4,1.5 L12,1.5 L16,5.5 L16,18.5 L4,18.5 Z M12,1.5 L12,5.5 L16,5.5"));
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                label.Length > 1 ? 8 : 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((20 - text.Width) / 2, 4.5));
        }
        dc.Pop();
    }
    private static SolidColorBrush Brush(string color)
    {
        var value = (Color)ColorConverter.ConvertFromString(color);
        if (System.Windows.Application.Current.TryFindResource("Background") is SolidColorBrush background &&
            background.Color.R * .299 + background.Color.G * .587 + background.Color.B * .114 >= 140)
            value = Color.FromRgb((byte)(value.R * .67), (byte)(value.G * .67), (byte)(value.B * .67));
        return new(value);
    }
}
