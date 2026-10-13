namespace StudioX.PortableDesktop;

using Avalonia.Controls;
using AvaloniaEdit.Document;
using StudioX.Application;

/// <summary>每个标签保留独立文本、撤销与光标；切换文件不覆盖未保存内容。</summary>
internal sealed class EditorBuffer(PortableDocument snapshot)
{
    public PortableDocument Snapshot { get; set; } = snapshot;
    public TextDocument Document { get; } = new(snapshot.Source.Text);
    public TextBlock Label { get; } = new();
    public Button SelectButton { get; } = new();
    public StackPanel Tab { get; } = new() { Orientation = Avalonia.Layout.Orientation.Horizontal };
    public bool IsDirty => Document.Text != Snapshot.Source.Text;
    public int CaretOffset
    {
        get; set;
    }
    public double VerticalOffset
    {
        get; set;
    }
    public double HorizontalOffset
    {
        get; set;
    }
}
