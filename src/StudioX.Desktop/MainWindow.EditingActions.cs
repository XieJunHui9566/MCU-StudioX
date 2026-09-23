namespace StudioX.Desktop;

using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;

public partial class MainWindow
{
    private bool CanEditSource => activeEditor is not null && WorkspaceTabs.SelectedItem == EditorTab && !SourceEditor.IsReadOnly;
    private string? CommentPrefix => CodeLanguage.ForFile(activeDocument?.RelativePath ?? "") switch { "C" or "C++" or "Verilog" => "//", "CMake" or "AGM Pin Map" => "#", _ => null };

    private void InitializeEditingActions()
    {
        CommandBindings.Add(new CommandBinding(SourceCommands.Undo, (_, _) => { SourceEditor.Undo(); SourceEditor.Focus(); },
            (_, e) => { e.CanExecute = CanEditSource && SourceEditor.CanUndo; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(SourceCommands.Redo, (_, _) => { SourceEditor.Redo(); SourceEditor.Focus(); },
            (_, e) => { e.CanExecute = CanEditSource && SourceEditor.CanRedo; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(SourceCommands.ToggleComment, (_, _) => ToggleSourceComment(),
            (_, e) => { e.CanExecute = CanEditSource && CommentPrefix is not null; e.Handled = true; }));
    }

    private void ToggleSourceComment()
    {
        if (!CanEditSource || CommentPrefix is not { } prefix) return;
        CloseCodeAssistance();
        var document = SourceEditor.Document;
        var start = SourceEditor.SelectionStart; var length = SourceEditor.SelectionLength;
        var first = document.GetLineByOffset(start);
        var last = document.GetLineByOffset(start + length);
        // 选择到下一行行首时，不把该行一起注释。
        if (length > 0 && last.Offset == start + length && last != first) last = last.PreviousLine!;
        var lines = new List<(DocumentLine Line, int Indent, string Content)>();
        for (var line = first; line is not null && line.LineNumber <= last.LineNumber; line = line.NextLine)
        {
            var text = document.GetText(line);
            var indent = text.TakeWhile(c => c is ' ' or '\t').Count();
            if (text.Length > indent) lines.Add((line, indent, text[indent..]));
        }
        if (lines.Count == 0) return;
        var remove = lines.All(line => line.Content.StartsWith(prefix, StringComparison.Ordinal));
        var caret = document.CreateAnchor(SourceEditor.CaretOffset); caret.SurviveDeletion = true; caret.MovementType = AnchorMovementType.AfterInsertion;
        using (document.RunUpdate())
        {
            foreach (var (line, indent, content) in lines.AsEnumerable().Reverse())
            {
                if (remove) document.Remove(line.Offset + indent, prefix.Length + (content.Length > prefix.Length && content[prefix.Length] == ' ' ? 1 : 0));
                else document.Insert(line.Offset + indent, prefix + " ");
            }
        }
        if (length > 0) SourceEditor.Select(first.Offset, last.EndOffset - first.Offset);
        else { SourceEditor.Select(caret.Offset, 0); SourceEditor.CaretOffset = caret.Offset; }
        SourceEditor.Focus();
    }
}
