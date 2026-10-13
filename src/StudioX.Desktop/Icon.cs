namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;

/// <summary>应用自绘的单色矢量符号，随 DPI 和主题缩放，不使用第三方品牌资产。</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("chip", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(Icon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind
    {
        get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value);
    }
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value);
    }
    // 小尺寸图标必须遵守布局约束，否则 WPF 仍按 18 像素绘制并裁掉右侧和底部。
    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(18, availableSize.Width), Math.Min(18, availableSize.Height));
    protected override void OnRender(DrawingContext dc)
    {
        var data = StudioX.Application.WorkbenchVisuals.IconPath(Kind);
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(Kind == "play" ? Foreground : null, new Pen(Foreground, 1.55) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse(data));
        dc.Pop();
    }
}
