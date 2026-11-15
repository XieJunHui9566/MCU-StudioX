namespace StudioX.Application.CodeIntelligence;

using System.Runtime.CompilerServices;

public static class CodePositions
{
    // 文本快照不可变；搜索、诊断和重构共用行索引，快照释放后索引也可回收。
    private static readonly ConditionalWeakTable<string, LineIndex> indexes = new();

    public static CodePosition FromOffset(string text, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, text.Length);
        var starts = indexes.GetValue(text, static value => new(value)).Starts;
        var line = Array.BinarySearch(starts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }
        return new(line, offset - starts[line]);
    }
    public static int ToOffset(string text, CodePosition position)
    {
        if (position.Line < 0 || position.Character < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        var starts = indexes.GetValue(text, static value => new(value)).Starts;
        if (position.Line >= starts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        var start = starts[position.Line];
        var lineEnd = position.Line + 1 < starts.Length ? starts[position.Line + 1] - 1 : text.Length;
        if (lineEnd > start && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }
        if (position.Character > lineEnd - start)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        return start + position.Character;
    }

    private sealed class LineIndex
    {
        public int[] Starts
        {
            get;
        }

        public LineIndex(string text)
        {
            var starts = new List<int> { 0 };
            var offset = 0;
            while ((offset = text.IndexOf('\n', offset)) >= 0)
            {
                starts.Add(++offset);
            }
            Starts = starts.ToArray();
        }
    }
}
