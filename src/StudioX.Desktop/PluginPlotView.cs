namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Media;

/// <summary>绘制插件提供的有限数值采样，不连接设备或执行脚本。</summary>
internal sealed class PluginPlotView : FrameworkElement
{
    private readonly Point[] points;

    public PluginPlotView(JsonElement? data)
    {
        var values = new List<Point>();
        if (data is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var sample in array.EnumerateArray().TakeLast(600))
            {
                double x;
                double y;
                if (sample.ValueKind == JsonValueKind.Object && sample.TryGetProperty("x", out var xv) && sample.TryGetProperty("y", out var yv) &&
                    xv.ValueKind == JsonValueKind.Number && yv.ValueKind == JsonValueKind.Number && xv.TryGetDouble(out x) && yv.TryGetDouble(out y))
                {
                    Add(x, y);
                }
                else if (sample.ValueKind == JsonValueKind.Array && sample.GetArrayLength() >= 2 &&
                    sample[0].ValueKind == JsonValueKind.Number && sample[1].ValueKind == JsonValueKind.Number &&
                    sample[0].TryGetDouble(out x) && sample[1].TryGetDouble(out y))
                {
                    Add(x, y);
                }
                else if (sample.ValueKind == JsonValueKind.Number && sample.TryGetDouble(out y))
                {
                    Add(values.Count, y);
                }
            }
        }
        points = values.ToArray();
        void Add(double x, double y)
        {
            if (double.IsFinite(x) && double.IsFinite(y))
            {
                values.Add(new Point(x, y));
            }
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var border = new Pen(TryFindResource("Border") as Brush ?? Brushes.Gray, 1);
        drawing.DrawRectangle(Brushes.Transparent, border, bounds);
        if (points.Length < 2 || ActualWidth < 4 || ActualHeight < 4)
        {
            return;
        }
        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            // 先按量级归一化，避免两个合法有限数相减溢出后把 NaN 送入 WPF 几何。
            static double Ratio(double value, double minimum, double maximum)
            {
                var divisor = Math.Max(1, Math.Max(Math.Abs(minimum), Math.Abs(maximum)));
                var start = minimum / divisor;
                var range = maximum / divisor - start;
                return range <= 0 ? 0.5 : Math.Clamp((value / divisor - start) / range, 0, 1);
            }
            Point Transform(Point point) => new(
                8 + Ratio(point.X, minX, maxX) * Math.Max(1, ActualWidth - 16),
                8 + (1 - Ratio(point.Y, minY, maxY)) * Math.Max(1, ActualHeight - 16));
            context.BeginFigure(Transform(points[0]), false, false);
            foreach (var point in points.Skip(1))
            {
                context.LineTo(Transform(point), true, false);
            }
        }
        geometry.Freeze();
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawGeometry(null, new Pen(TryFindResource("Accent") as Brush ?? Brushes.CornflowerBlue, 2), geometry);
        drawing.Pop();
    }
}
