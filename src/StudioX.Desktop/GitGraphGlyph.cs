namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;

/// <summary>轻量 WPF 提交图标；每行由 GitGraphLayout 计算，绘制由当前主题承载。</summary>
public sealed class GitGraphGlyph : FrameworkElement
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GitGraphRow), typeof(GitGraphGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // 兼容把布局属性命名为 Layout 的调用方。
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(GitGraphRow), typeof(GitGraphGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public GitGraphRow? Row
    {
        get => (GitGraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public GitGraphRow? Layout
    {
        get => (GitGraphRow?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private static readonly Brush[] Colors =
    [
        MakeBrush(0x62, 0xA9, 0xF5), MakeBrush(0xE8, 0xA4, 0x62),
        MakeBrush(0xA4, 0x86, 0xE8), MakeBrush(0x69, 0xBD, 0xA5),
        MakeBrush(0xE5, 0x7F, 0x94), MakeBrush(0xD3, 0xB1, 0x61),
        MakeBrush(0x8A, 0xB8, 0xDA), MakeBrush(0xB5, 0xC6, 0x75)
    ];

    private static Brush MakeBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var row = Row ?? Layout;
        if (row is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var spacing = Math.Min(16d, Math.Max(1d, (ActualWidth - 24d) / Math.Max(1, row.TotalLaneCount - 1)));
        double X(int lane) => 12d + lane * spacing;
        double Y(double level) => level * ActualHeight;

        foreach (var segment in row.Segments)
        {
            var start = new Point(X(segment.StartLane), Y(segment.StartLevel));
            var end = new Point(X(segment.EndLane), Y(segment.EndLevel));
            var pen = new Pen(Colors[Math.Abs(segment.ColorIndex % Colors.Length)], 2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            if (segment.StartLane == segment.EndLane)
            {
                drawingContext.DrawLine(pen, start, end);
            }
            else
            {
                var bend = (end.Y - start.Y) * 0.52;
                var curve = new StreamGeometry();
                using (var context = curve.Open())
                {
                    context.BeginFigure(start, false, false);
                    context.BezierTo(new(start.X, start.Y + bend), new(end.X, end.Y - bend), end, true, false);
                }
                curve.Freeze();
                drawingContext.DrawGeometry(null, pen, curve);
            }
        }

        var center = new Point(X(row.NodeLane), ActualHeight / 2);
        var radius = row.IsMerge ? 5d : 4d;
        drawingContext.DrawEllipse(Colors[Math.Abs(row.NodeColorIndex % Colors.Length)], null, center, radius, radius);
    }
}
