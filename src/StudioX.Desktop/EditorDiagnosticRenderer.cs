namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using StudioX.Application;

internal sealed record EditorDiagnostic(BuildDiagnostic Diagnostic, int Offset, int Length) : ISegment
{
    public int EndOffset => Offset + Length;
}

internal sealed class EditorDiagnosticRenderer(TextView view) : IBackgroundRenderer
{
    private static readonly Pen ErrorPen = CreatePen("#FF6475");
    private static readonly Pen WarningPen = CreatePen("#E8B85B");
    public KnownLayer Layer => KnownLayer.Selection;
    public IReadOnlyList<EditorDiagnostic> Markers { get; private set; } = [];
    public void Set(IReadOnlyList<EditorDiagnostic> markers) { Markers = markers; view.InvalidateLayer(Layer); }
    private static Pen CreatePen(string color)
    {
        var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), 1.4);
        pen.Freeze(); return pen;
    }
    public void Draw(TextView textView, DrawingContext dc)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        var first = textView.VisualLines[0].FirstDocumentLine.Offset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (var marker in Markers.Where(m => m.EndOffset >= first && m.Offset <= last))
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
            {
                var left = Math.Max(0, rect.Left); var right = Math.Min(textView.ActualWidth, Math.Max(rect.Right, rect.Left + 5));
                if (left >= right) continue;
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(left, rect.Bottom - 1), false, false);
                    var up = true;
                    for (var x = left + 2; x < right + 2; x += 2)
                    { context.LineTo(new Point(Math.Min(x, right), rect.Bottom - (up ? 3 : 1)), true, false); up = !up; }
                }
                dc.DrawGeometry(null, marker.Diagnostic.IsWarning ? WarningPen : ErrorPen, geometry);
            }
    }
    public static EditorDiagnostic? Locate(TextDocument document, BuildDiagnostic diagnostic)
    {
        if (diagnostic.Line < 1 || diagnostic.Line > document.LineCount) return null;
        var line = document.GetLineByNumber(diagnostic.Line);
        var text = document.GetText(line); var index = 0; var column = 1;
        // GCC 默认显示列：制表符按 8 列展开，UTF-16 代理对不是两列。
        while (index < text.Length && column < diagnostic.Column)
        {
            var rune = System.Text.Rune.TryGetRuneAt(text, index, out var value) ? value : System.Text.Rune.ReplacementChar;
            column += rune.Value == '\t' ? 8 - (column - 1) % 8 : DisplayWidth(rune);
            index += rune.Utf16SequenceLength;
        }
        if (diagnostic.Column == 0) { while (index < text.Length && char.IsWhiteSpace(text[index])) index++; }
        if (index >= text.Length) index = Math.Max(0, text.Length - 1);
        var end = index;
        if (diagnostic.Column == 0) end = text.Length;
        else
        {
            while (index > 0 && Identifier(text[index]) && Identifier(text[index - 1])) index--;
            while (end < text.Length && Identifier(text[end])) end++;
            if (end == index && end < text.Length) end++;
        }
        return new(diagnostic, line.Offset + index, end - index);
    }
    private static bool Identifier(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static int DisplayWidth(System.Text.Rune rune)
    {
        if (System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark) return 0;
        var c = rune.Value;
        return c is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFF ? 2 : 1;
    }
}
