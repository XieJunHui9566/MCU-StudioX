namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Media;

/// <summary>工作台演示视图；只接收已订阅数据，不拥有设备连接。</summary>
public sealed class SignalPlot : FrameworkElement
{
    private readonly Queue<(double Value, int State)> samples = new();
    public void Add(double value, int state) { samples.Enqueue((value, state)); while (samples.Count > 160) samples.Dequeue(); InvalidateVisual(); }
    public void Clear() { samples.Clear(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var background = Resource("Background"); var border = Resource("Border"); var muted = Resource("Muted"); var accent = Resource("Accent");
        dc.DrawRoundedRectangle(background, new Pen(border, 1), new Rect(0, 0, ActualWidth, ActualHeight), 6, 6);
        var width = Math.Max(1, ActualWidth - 82); var height = Math.Max(1, ActualHeight - 130);
        for (var i = 0; i <= 4; i++)
        {
            var y = 46 + i * height / 4;
            dc.DrawLine(new Pen(border, 0.6), new Point(52, y), new Point(52 + width, y));
            Label((32 - i * 4).ToString(CultureInfo.InvariantCulture), 14, y - 8, muted);
        }
        Label("TEMPERATURE / °C", 16, 14, muted);
        Label("DIGITAL / 0 · 1", 16, 64 + height, muted);
        var values = samples.ToArray();
        if (values.Length < 2) { Label("连接模拟设备后显示曲线与状态波形", 90, 80, muted); return; }
        for (var i = 1; i < values.Length; i++)
        {
            var x0 = 52 + (i - 1) * width / 159; var x1 = 52 + i * width / 159;
            double Y(double v) => 46 + (32 - Math.Clamp(v, 16, 32)) / 16 * height;
            dc.DrawLine(new Pen(accent, 2), new Point(x0, Y(values[i - 1].Value)), new Point(x1, Y(values[i].Value)));
            var previous = ActualHeight - 20 - values[i - 1].State * 22;
            var current = ActualHeight - 20 - values[i].State * 22;
            dc.DrawLine(new Pen(muted, 1.5), new Point(x0, previous), new Point(x1, previous));
            dc.DrawLine(new Pen(muted, 1.5), new Point(x1, previous), new Point(x1, current));
        }
        void Label(string text, double x, double y, Brush color) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 11, color, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    }
    private Brush Resource(string name) => (Brush)(TryFindResource(name) ?? Brushes.Gray);
}
