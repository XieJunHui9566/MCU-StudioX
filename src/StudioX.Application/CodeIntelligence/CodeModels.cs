namespace StudioX.Application.CodeIntelligence;

public sealed record CodePosition(int Line, int Character);
public sealed record CodeRange(CodePosition Start, CodePosition End);
public sealed record CodeSuggestion(string Label, string InsertText, string FilterText, string Detail,
    string Documentation, int Kind, CodeRange? Range, string SortText);
public sealed record CodeSignature(string Label, string Documentation, IReadOnlyList<string> Parameters, int ActiveParameter);
public sealed record CodeDocumentSnapshot(string Path, string Text);
public sealed record CodeLocation(string DocumentPath, CodeRange Range, string DisplayPath);
public sealed record CodeHover(string Contents, IReadOnlyList<CodeLocation> Declarations);

public static class CodePositions
{
    public static CodePosition FromOffset(string text, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, text.Length);
        var line = 0; var start = 0;
        for (var i = 0; i < offset; i++) if (text[i] == '\n') { line++; start = i + 1; }
        return new(line, offset - start);
    }
    public static int ToOffset(string text, CodePosition position)
    {
        if (position.Line < 0 || position.Character < 0) throw new ArgumentOutOfRangeException(nameof(position));
        var start = 0;
        for (var line = 0; line < position.Line; line++)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) throw new ArgumentOutOfRangeException(nameof(position));
            start = end + 1;
        }
        var lineEnd = text.IndexOf('\n', start);
        if (lineEnd < 0) lineEnd = text.Length;
        if (lineEnd > start && text[lineEnd - 1] == '\r') lineEnd--;
        if (position.Character > lineEnd - start) throw new ArgumentOutOfRangeException(nameof(position));
        return start + position.Character;
    }
}
