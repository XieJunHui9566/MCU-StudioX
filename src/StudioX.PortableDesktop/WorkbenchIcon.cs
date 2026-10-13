namespace StudioX.PortableDesktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StudioX.Application;

/// <summary>仅绘制共用路径；同一符号在 WPF 和 Avalonia 上保持相同轮廓。</summary>
internal sealed class WorkbenchIcon(string kind, IBrush? color = null) : Control
{
    public override void Render(DrawingContext context)
    {
        var brush = color ?? this.FindResource("TextBrush") as IBrush ?? Brushes.Gray;
        var geometry = Geometry.Parse(WorkbenchVisuals.IconPath(kind));
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(kind == "play" ? brush : null, new Pen(brush, 1.55, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
