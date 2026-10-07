namespace StudioX.Desktop;

using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using StudioX.Application.CodeIntelligence;

/// <summary>在语法与括号配色之后降低未参与编译文字的亮度，保留原有语法色和编辑行为。</summary>
internal sealed class InactiveCodeColorizer : DocumentColorizingTransformer, IDisposable
{
    private readonly TextEditor editor;
    private readonly Dictionary<Brush, Brush> brushes = [];
    private TextDocument? document;
    private CodeInactiveRegionBatch? batch;
    private (int Start, int End)[] spans = [];
    private bool dark;
    internal IReadOnlyList<(int Start, int End)> Spans => spans;

    public InactiveCodeColorizer(TextEditor editor)
    {
        this.editor = editor;
        editor.TextChanged += Changed;
        editor.DocumentChanged += Changed;
        MoveLast();
    }

    public void Set(CodeInactiveRegionBatch? next, bool darkTheme)
    {
        if (next is not null && editor.Document.Text != next.Text)
        {
            next = null;
        }
        var reordered = MoveLast();
        if (ReferenceEquals(batch, next) && document == editor.Document && dark == darkTheme)
        {
            if (reordered)
            {
                editor.TextArea.TextView.Redraw();
            }
            return;
        }
        batch = next;
        document = editor.Document;
        dark = darkTheme;
        brushes.Clear();
        spans = next?.Regions.Select(range => (CodePositions.ToOffset(next.Text, range.Start), CodePositions.ToOffset(next.Text, range.End))).ToArray() ?? [];
        editor.TextArea.TextView.Redraw();
    }

    private bool MoveLast()
    {
        var transformers = editor.TextArea.TextView.LineTransformers;
        if (transformers.Count > 0 && ReferenceEquals(transformers[^1], this))
        {
            return false;
        }
        transformers.Remove(this);
        transformers.Add(this);
        return true;
    }

    private void Changed(object? sender, EventArgs e)
    {
        // 文本变化后不能把旧范围锚到新文本；依赖宏变化由工作区分析失效机制统一撤销。
        batch = null;
        spans = [];
        brushes.Clear();
        editor.TextArea.TextView.Redraw();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (document != editor.Document)
        {
            return;
        }
        var low = 0;
        var high = spans.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (spans[mid].End <= line.Offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        for (var i = low; i < spans.Length && spans[i].Start < line.EndOffset; i++)
        {
            var start = Math.Max(line.Offset, spans[i].Start);
            var end = Math.Min(line.EndOffset, spans[i].End);
            if (end <= start)
            {
                continue;
            }
            ChangeLinePart(start, end, element =>
            {
                var source = element.TextRunProperties.ForegroundBrush ?? editor.Foreground;
                if (!brushes.TryGetValue(source, out var dimmed))
                {
                    dimmed = source.CloneCurrentValue();
                    dimmed.Opacity *= dark ? .48 : .55;
                    if (dimmed.CanFreeze)
                    {
                        dimmed.Freeze();
                    }
                    brushes[source] = dimmed;
                }
                element.TextRunProperties.SetForegroundBrush(dimmed);
            });
        }
    }

    public void Dispose()
    {
        editor.TextChanged -= Changed;
        editor.DocumentChanged -= Changed;
        editor.TextArea.TextView.LineTransformers.Remove(this);
        brushes.Clear();
    }
}
