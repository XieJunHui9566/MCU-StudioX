namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application.Output;

/// <summary>可选择复制的彩色只读输出，复用 IDE 现有文本渲染器。</summary>
public sealed class OutputLogView : TextEditor
{
    private TextAnchor? progressAnchor;
    public OutputLogView()
    {
        IsReadOnly = true;
        Document.UndoStack.SizeLimit = 0;
        ShowLineNumbers = false;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        TextArea.TextView.LineTransformers.Add(new OutputLogColorizer(this));
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy, CommandTarget = TextArea });
        menu.Items.Add(new MenuItem { Header = "全选", Command = ApplicationCommands.SelectAll, CommandTarget = TextArea });
        ContextMenu = menu;
        TextChanged += (_, _) => { if (Document.TextLength == 0) {progressAnchor = null;} };
    }

    /// <summary>诊断插在活动进度行之前，后续刷新不会覆盖它们。</summary>
    public void AppendLogLine(string text)
    {
        if (ProgressLine() is { } line) {Document.Insert(line.Offset, text.TrimEnd('\r', '\n') + "\n");}
        else {AppendText((Document.TextLength > 0 && !Text.EndsWith('\n') ? "\n" : "") + text.TrimEnd('\r', '\n') + "\n");}
    }

    public void UpdateProgress(string text)
    {
        var scroll = Template?.FindName("PART_ScrollViewer", this) as ScrollViewer;
        var follow = scroll is null || scroll.VerticalOffset >= scroll.ExtentHeight - scroll.ViewportHeight - 12;
        if (ProgressLine() is { } line) {Document.Replace(line.Offset, line.Length, text);}
        else
        {
            if (Document.TextLength > 0 && !Text.EndsWith('\n')) {AppendText("\n");}
            var offset = Document.TextLength;
            AppendText(text + "\n");
            progressAnchor = Document.CreateAnchor(offset);
            progressAnchor.MovementType = AnchorMovementType.AfterInsertion;
            progressAnchor.SurviveDeletion = true;
        }
        if (follow) {ScrollToEnd();}
    }

    public void CommitProgress() => progressAnchor = null;
    private DocumentLine? ProgressLine() => progressAnchor is { IsDeleted: false } anchor && anchor.Offset < Document.TextLength
        ? Document.GetLineByOffset(anchor.Offset) : null;
    internal Brush BrushFor(OutputTone tone) => TryFindResource(tone switch
    {
        OutputTone.Information => "Accent", OutputTone.Success => "DiagnosticSuccess",
        OutputTone.Warning => "DiagnosticWarning", OutputTone.Error => "DiagnosticError", _ => "Text"
    }) as Brush ?? Foreground;
    public void RefreshTheme() => TextArea.TextView.Redraw();
}
