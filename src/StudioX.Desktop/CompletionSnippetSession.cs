namespace StudioX.Desktop;

using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using StudioX.Application.CodeIntelligence;

/// <summary>单次补全的占位符导航；锚点随输入移动，切换文档或撤销后立即释放。</summary>
internal sealed class CompletionSnippetSession : TextAreaStackedInputHandler
{
    private readonly TextDocument document;
    private readonly (TextAnchor Start, TextAnchor End)[] stops;
    private readonly TextAnchor start;
    private readonly TextAnchor end;
    private int current;
    private bool selecting;

    private CompletionSnippetSession(TextArea area, CodeSnippet snippet, int offset) : base(area)
    {
        document = area.Document;
        TextAnchor Anchor(int position, AnchorMovementType movement)
        {
            var anchor = document.CreateAnchor(position);
            anchor.MovementType = movement;
            return anchor;
        }
        start = Anchor(offset, AnchorMovementType.BeforeInsertion);
        end = Anchor(offset + snippet.Text.Length, AnchorMovementType.AfterInsertion);
        stops = snippet.Stops.Select(stop => (
            Anchor(offset + stop.Offset, AnchorMovementType.BeforeInsertion),
            Anchor(offset + stop.Offset + stop.Length, AnchorMovementType.AfterInsertion))).ToArray();
    }

    public static void Insert(TextArea area, CodeSnippet snippet, int offset, int length, bool indentBlocks)
    {
        var line = area.Document.GetLineByOffset(offset);
        var prefix = area.Document.GetText(line.Offset, offset - line.Offset);
        var indentation = new string(prefix.TakeWhile(c => c is ' ' or '\t').ToArray());
        snippet = snippet.PrepareInsertion(indentation, TextUtilities.GetNewLineFromDocument(area.Document, line.LineNumber), area.Options.IndentationString, indentBlocks);
        foreach (var previous in area.StackedInputHandlers.OfType<CompletionSnippetSession>().ToArray())
        {
            area.PopStackedInputHandler(previous);
        }
        using (area.Document.RunUpdate())
        {
            area.Document.Replace(offset, length, snippet.Text);
            var session = new CompletionSnippetSession(area, snippet, offset);
            if (snippet.Stops.All(stop => stop.Index == 0))
            {
                session.SelectCurrent();
            }
            else
            {
                area.PushStackedInputHandler(session);
            }
        }
    }

    public override void Attach()
    {
        base.Attach();
        document.UpdateFinished += DocumentUpdated;
        TextArea.DocumentChanged += DocumentChanged;
        TextArea.Caret.PositionChanged += CaretChanged;
        SelectCurrent();
    }

    public override void Detach()
    {
        document.UpdateFinished -= DocumentUpdated;
        TextArea.DocumentChanged -= DocumentChanged;
        TextArea.Caret.PositionChanged -= CaretChanged;
        base.Detach();
    }

    public override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            Move(Keyboard.Modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
        else if (e.Key is Key.Escape or Key.Enter)
        {
            TextArea.PopStackedInputHandler(this);
            TextArea.Selection = Selection.Create(TextArea, TextArea.Caret.Offset, TextArea.Caret.Offset);
            e.Handled = true;
        }
    }

    internal void Move(bool backwards)
    {
        if (stops.Any(stop => stop.Start.IsDeleted || stop.End.IsDeleted))
        {
            TextArea.PopStackedInputHandler(this);
            return;
        }
        current = backwards ? Math.Max(0, current - 1) : Math.Min(stops.Length - 1, current + 1);
        SelectCurrent();
        if (current == stops.Length - 1)
        {
            TextArea.PopStackedInputHandler(this);
        }
    }

    private void SelectCurrent()
    {
        selecting = true;
        try
        {
            var stop = stops[current];
            TextArea.Caret.Offset = stop.End.Offset;
            TextArea.Selection = Selection.Create(TextArea, stop.Start.Offset, stop.End.Offset);
        }
        finally
        {
            selecting = false;
        }
    }

    private void DocumentChanged(object? sender, EventArgs e) => TextArea.PopStackedInputHandler(this);

    private void DocumentUpdated(object? sender, EventArgs e)
    {
        if (start.IsDeleted || end.IsDeleted || start.Offset >= end.Offset || stops.Any(stop => stop.Start.IsDeleted || stop.End.IsDeleted))
        {
            TextArea.PopStackedInputHandler(this);
        }
    }

    private void CaretChanged(object? sender, EventArgs e)
    {
        if (!selecting && !document.IsInUpdate && (start.IsDeleted || end.IsDeleted || TextArea.Caret.Offset < start.Offset || TextArea.Caret.Offset > end.Offset))
        {
            TextArea.PopStackedInputHandler(this);
        }
    }
}
