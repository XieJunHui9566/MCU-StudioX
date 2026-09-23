namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

/// <summary>在清晰文字下面绘制同色光晕，不对背景、选区或原始文字应用模糊。</summary>
internal sealed class EditorGlowLayer : FrameworkElement
{
    private readonly TextView view;
    private readonly BlurEffect blur = new() { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };

    public EditorGlowLayer(TextView view)
    {
        this.view = view;
        IsHitTestVisible = false;
        Focusable = false;
        Effect = blur;
        view.InsertLayer(this, KnownLayer.Text, LayerInsertionPosition.Below);
        view.VisualLinesChanged += (_, _) => InvalidateVisual();
        view.ScrollOffsetChanged += (_, _) => InvalidateVisual();
    }

    public void Configure(bool enabled, double strength, double fontSize)
    {
        Visibility = enabled && strength > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 强度只调亮度，并限制最浓的光晕；避免旧设置 80–100% 将字边叠成一片。
        Opacity = Math.Clamp(strength / 100, 0, 1) * .25;
        // 扩散范围随字号缓慢变化，不随强度扩大，保持相邻字符间的空隙。
        blur.Radius = Math.Clamp(fontSize * .09, 1, 2);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!view.VisualLinesValid) return;
        // 复用编辑器已经排版的可见行，自动保持语法颜色、字号、缩进与滚动位置一致。
        foreach (var line in view.VisualLines)
        {
            var y = line.VisualTop - view.ScrollOffset.Y;
            foreach (var textLine in line.TextLines)
            {
                textLine.Draw(drawingContext, new Point(-view.ScrollOffset.X, y), InvertAxes.None);
                y += textLine.Height;
            }
        }
    }
}
