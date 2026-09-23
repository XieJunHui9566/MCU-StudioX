namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using StudioX.Application.CodeIntelligence;

internal sealed class CodeCompletionData : ICompletionData
{
    private readonly TextDocument document;
    private readonly TextAnchor start;
    private readonly TextAnchor end;
    private readonly CodeSuggestion suggestion;
    private readonly Action<bool> inserted;
    public CodeCompletionData(TextDocument document, CodeSuggestion suggestion, int startOffset, int endOffset, int priority, Action<bool> inserted)
    {
        this.document = document; this.suggestion = suggestion; this.inserted = inserted;
        start = document.CreateAnchor(startOffset); start.MovementType = AnchorMovementType.BeforeInsertion;
        end = document.CreateAnchor(endOffset); end.MovementType = AnchorMovementType.AfterInsertion;
        Priority = priority;
        Content = CreateRow(suggestion);
        var description = new StackPanel { MaxWidth = 520, Margin = new Thickness(8) };
        description.Children.Add(new TextBlock { Text = suggestion.Label.Trim(), FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap });
        description.Children.Add(new TextBlock { Text = suggestion.Detail, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (suggestion.Documentation.Length > 0) description.Children.Add(new TextBlock { Text = suggestion.Documentation, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        Description = description;
    }
    public ImageSource? Image => null;
    public string Text => suggestion.FilterText.Trim();
    public object Content { get; }
    public object Description { get; }
    public double Priority { get; }
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (textArea.Document != document || start.IsDeleted || end.IsDeleted) return;
        var offset = start.Offset;
        var finish = Math.Max(end.Offset, completionSegment.EndOffset);
        if (finish < offset || finish > document.TextLength) return;
        var text = suggestion.InsertText;
        var function = suggestion.Kind is 2 or 3;
        var next = finish;
        while (next < document.TextLength && document.GetCharAt(next) is ' ' or '\t') next++;
        var addParentheses = function && !text.Contains('(') && (next == document.TextLength || document.GetCharAt(next) != '(');
        if (addParentheses) text += "()";
        using (document.RunUpdate())
        {
            document.Replace(offset, finish - offset, text);
            textArea.Caret.Offset = offset + text.Length - (addParentheses ? 1 : 0);
            textArea.Selection = Selection.Create(textArea, textArea.Caret.Offset, textArea.Caret.Offset);
        }
        inserted(addParentheses);
    }
    private static object CreateRow(CodeSuggestion suggestion)
    {
        var (symbol, color) = suggestion.Kind switch
        {
            2 or 3 => ("ƒ", "#E2BF83"), 5 or 10 => ("m", "#C59DD8"), 6 => ("v", "#83B7EB"),
            7 or 8 or 13 or 22 or 25 => ("T", "#6FB7B2"), 14 => ("k", "#CF8E6D"),
            21 => ("#", "#C59DD8"), 17 or 19 => ("h", "#8FBC8F"), _ => ("·", "#9DA0A8")
        };
        var grid = new Grid { Margin = new Thickness(7, 4, 7, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = symbol, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) });
        var label = new TextBlock { Text = suggestion.Label.Trim(), FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 370 };
        Grid.SetColumn(label, 1); grid.Children.Add(label);
        var detail = new TextBlock { Text = suggestion.Detail, FontSize = 11, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 145, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(detail, 2); grid.Children.Add(detail);
        return grid;
    }
}
