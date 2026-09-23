namespace StudioX.Desktop;

using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application;

/// <summary>每个标签持有自己的文本、保存基线和视图位置；编辑控件可复用，撤销栈不可共用。</summary>
internal sealed class EditorDocumentSession(SourceDocument source)
{
    public SourceDocument Source { get; set; } = source;
    public TextDocument Buffer { get; } = new(source.Text);
    public TabItem Tab { get; } = new() { Padding = new(10, 4, 8, 4), Height = 34 };
    public TextBlock Label { get; } = new() { VerticalAlignment = System.Windows.VerticalAlignment.Center, MaxWidth = 240, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
    public EventHandler? Changed { get; set; }
    public int CaretOffset { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }
    public bool IsDirty => !string.Equals(Buffer.Text, Source.Text, StringComparison.Ordinal);
}
