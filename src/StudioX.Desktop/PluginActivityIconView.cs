namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using StudioX.Extensions;

/// <summary>绘制插件提供的折线数据；不加载文件、XAML、SVG 文档或插件程序集。</summary>
internal sealed class PluginActivityIconView : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(PluginActivityIconView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly StreamGeometry geometry = new();

    public PluginActivityIconView(PluginActivityIcon icon)
    {
        icon.Validate();
        using (var context = geometry.Open())
        {
            foreach (var stroke in icon.Strokes)
            {
                context.BeginFigure(new Point(stroke[0], stroke[1]), false, false);
                for (var index = 2; index < stroke.Length; index += 2)
                    context.LineTo(new Point(stroke[index], stroke[index + 1]), true, false);
            }
        }
        geometry.Freeze();
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(18, availableSize.Width), Math.Min(18, availableSize.Height));

    protected override void OnRender(DrawingContext drawing)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        drawing.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        drawing.PushTransform(new ScaleTransform(side / 24, side / 24));
        drawing.DrawGeometry(null, new Pen(Foreground, 1.55) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
        drawing.Pop();
        drawing.Pop();
    }
}
