namespace StudioX.Desktop;

using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using StudioX.Application.Output;

/// <summary>只着色当前可见行；原文、选择与剪贴板内容保持为纯文本。</summary>
internal sealed class OutputLogColorizer(OutputLogView editor) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) {return;}
        var text = CurrentContext.Document.GetText(line);
        var tone = BuildOutputParser.Tone(text);
        var color = editor.BrushFor(tone);
        ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(color));
        if (text.Length >= 10 && text[0] == '[' && text[3] == ':' && text[6] == ':' && text[9] == ']')
        {
            var muted = editor.TryFindResource("Muted") as Brush ?? editor.Foreground;
            ChangeLinePart(line.Offset, line.Offset + 10, element => element.TextRunProperties.SetForegroundBrush(muted));
        }
    }
}
