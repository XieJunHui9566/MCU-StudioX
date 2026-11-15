namespace StudioX.Application.CodeIntelligence;

using System.Text;

/// <summary>clangd 的编号占位符片段；不解释变量、嵌套表达式或执行命令。</summary>
public sealed record CodeSnippet(string Text, IReadOnlyList<CodeSnippetStop> Stops)
{
    public static CodeSnippet? Parse(string source)
    {
        if (source.Length > 65_536)
        {
            return null;
        }
        var text = new StringBuilder();
        var stops = new List<CodeSnippetStop>();
        var indices = new HashSet<int>();
        for (var position = 0; position < source.Length;)
        {
            var character = source[position++];
            if (character == '\\' && position < source.Length && source[position] is '$' or '}' or '\\')
            {
                text.Append(source[position++]);
                continue;
            }
            if (character != '$')
            {
                text.Append(character);
                continue;
            }
            var braced = position < source.Length && source[position] == '{';
            if (braced)
            {
                position++;
            }
            var numberStart = position;
            while (position < source.Length && char.IsAsciiDigit(source[position]))
            {
                position++;
            }
            if (position == numberStart || !int.TryParse(source.AsSpan(numberStart, position - numberStart), out var index) ||
                !indices.Add(index) || stops.Count >= 128)
            {
                // clangd 只生成独立编号；未知语法整项拒绝，不能把协议标记写入源码。
                return null;
            }
            var start = text.Length;
            if (braced)
            {
                if (position < source.Length && source[position] == ':')
                {
                    position++;
                    while (position < source.Length && source[position] != '}')
                    {
                        character = source[position++];
                        if (character == '\\' && position < source.Length && source[position] is '$' or '}' or '\\')
                        {
                            text.Append(source[position++]);
                        }
                        else if (character == '$')
                        {
                            return null;
                        }
                        else
                        {
                            text.Append(character);
                        }
                    }
                }
                if (position >= source.Length || source[position++] != '}')
                {
                    return null;
                }
            }
            stops.Add(new(index, start, text.Length - start));
        }
        if (!indices.Contains(0))
        {
            stops.Add(new(0, text.Length, 0));
        }
        return new(text.ToString(), stops.OrderBy(stop => stop.Index == 0 ? long.MaxValue : stop.Index).ToArray());
    }

    /// <summary>沿用插入行的缩进和换行；只为语句片段补齐 clangd 未提供的语句体缩进。</summary>
    public CodeSnippet PrepareInsertion(string indentation, string newLine, string tab, bool indentBlocks)
    {
        var result = new StringBuilder();
        void CheckCapacity(long additional)
        {
            if (result.Length + additional > 1024 * 1024)
            {
                throw new InvalidDataException("代码片段展开后超过 1,048,576 个字符，请缩小缩进或片段内容。");
            }
        }
        var offsets = new int[Text.Length + 1];
        var depth = 0;
        var lineStart = 0;
        for (var position = 0; position < Text.Length; position++)
        {
            if (position == lineStart && position > 0)
            {
                var closing = Text[position] == '}';
                if (indentBlocks && closing)
                {
                    depth = Math.Max(0, depth - 1);
                }
                // 已有相对缩进时保留服务端排版；不重复增加。
                var levels = indentBlocks && Text[position] is not (' ' or '\t') ? depth : 0;
                CheckCapacity(indentation.Length + (long)levels * tab.Length);
                result.Append(indentation);
                for (var level = 0; level < levels; level++)
                {
                    result.Append(tab);
                }
            }
            offsets[position] = result.Length;
            var character = Text[position];
            if (character is '\r' or '\n')
            {
                if (indentBlocks && Text.AsSpan(lineStart, position - lineStart).TrimEnd().EndsWith("{"))
                {
                    depth++;
                }
                CheckCapacity(newLine.Length);
                result.Append(newLine);
                if (character == '\r' && position + 1 < Text.Length && Text[position + 1] == '\n')
                {
                    position++;
                    offsets[position] = offsets[position - 1];
                }
                lineStart = position + 1;
            }
            else if (character == '\t')
            {
                CheckCapacity(tab.Length);
                result.Append(tab);
            }
            else
            {
                CheckCapacity(1);
                result.Append(character);
            }
        }
        offsets[Text.Length] = result.Length;
        return new(result.ToString(), Stops.Select(stop => new CodeSnippetStop(stop.Index, offsets[stop.Offset], offsets[stop.Offset + stop.Length] - offsets[stop.Offset])).ToArray());
    }
}
