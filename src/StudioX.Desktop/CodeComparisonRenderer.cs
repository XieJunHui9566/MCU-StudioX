namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

/// <summary>差异只绘制背景和边栏，保留源码语法的前景色。</summary>
internal sealed class CodeComparisonRenderer : IBackgroundRenderer
{
    internal IReadOnlyList<TextSegment> Ranges { get; set; } = [];
    internal Brush Color { get; set; } = Brushes.Transparent;
    internal double LineOpacity { get; set; } = .30;
    internal double CharacterOpacity { get; set; } = .52;
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView view, DrawingContext drawing)
    {
        if (!view.VisualLinesValid || view.VisualLines.Count == 0 || Ranges.Count == 0)
        {
            return;
        }
        var first = view.VisualLines[0].FirstDocumentLine.Offset;
        var last = view.VisualLines[^1].LastDocumentLine.EndOffset;
        var start = 0;
        var end = Ranges.Count;
        while (start < end)
        {
            var middle = start + (end - start) / 2;
            if (Ranges[middle].EndOffset < first)
            {
                start = middle + 1;
            }
            else
            {
                end = middle;
            }
        }
        var visible = new List<TextSegment>();
        for (var i = start; i < Ranges.Count && Ranges[i].StartOffset <= last; i++)
        {
            visible.Add(Ranges[i]);
        }
        foreach (var line in view.VisualLines)
        {
            if (!visible.Any(range => range.StartOffset <= line.LastDocumentLine.EndOffset &&
                (range.Length == 0 ? range.StartOffset >= line.FirstDocumentLine.Offset : range.EndOffset > line.FirstDocumentLine.Offset)))
            {
                continue;
            }
            var top = line.VisualTop - view.ScrollOffset.Y;
            drawing.PushOpacity(LineOpacity);
            drawing.DrawRectangle(Color, null, new Rect(0, top, view.ActualWidth, line.Height));
            drawing.Pop();
            drawing.DrawRectangle(Color, null, new Rect(0, top, 3, line.Height));
        }
        drawing.PushOpacity(CharacterOpacity);
        foreach (var range in visible)
        {
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(view, range))
            {
                drawing.DrawRectangle(Color, null, new Rect(rect.Left, rect.Top, Math.Max(3, rect.Width), rect.Height));
            }
        }
        drawing.Pop();
    }
}
