namespace StudioX.Desktop;

using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using StudioX.Application.Editing;

internal sealed class CodeTemplateCompletionData : ICompletionData
{
    private readonly TextDocument document;
    private readonly TextAnchor start;
    private readonly TextAnchor end;
    private readonly Action<int, int> insert;
    public CodeTemplateCompletionData(TextDocument document, CodeTemplateEntry entry, int startOffset, int endOffset, Action<int, int> insert)
    {
        this.document = document; this.insert = insert;
        start = document.CreateAnchor(startOffset); start.MovementType = AnchorMovementType.BeforeInsertion;
        end = document.CreateAnchor(endOffset); end.MovementType = AnchorMovementType.AfterInsertion;
        Text = entry.Template.Shortcut;
        Content = new TextBlock { Text = Text + "  ·  " + entry.Template.Name + "  ·  " + entry.ScopeLabel + "模板", Margin = new(7, 4, 7, 4) };
        Description = new TextBlock { Text = entry.Template.Description + "\n\n" + entry.Template.Body, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = System.Windows.TextWrapping.Wrap, MaxWidth = 500, MaxHeight = 320, Margin = new(8) };
    }
    public ImageSource? Image => null;
    public string Text { get; }
    public object Content { get; }
    public object Description { get; }
    public double Priority => 1000;
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (textArea.Document != document || start.IsDeleted || end.IsDeleted) { return; }
        var finish = Math.Max(end.Offset, completionSegment.EndOffset);
        while (finish < document.TextLength && (char.IsLetterOrDigit(document.GetCharAt(finish)) || document.GetCharAt(finish) == '_')) { finish++; }
        if (finish >= start.Offset && finish <= document.TextLength) { insert(start.Offset, finish - start.Offset); }
    }
}
