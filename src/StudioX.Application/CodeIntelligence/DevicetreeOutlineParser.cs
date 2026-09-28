namespace StudioX.Application.CodeIntelligence;

/// <summary>从当前设备树源文件提取可导航的节点和属性，不展开 include，也不合成最终设备树。</summary>
public static class DevicetreeOutlineParser
{
    public const int NodeKind = 255;
    public const int PropertyKind = 256;

    private const int MaxSymbols = 3000;
    private const int MaxDepth = 128;

    public static IReadOnlyList<CodeDocumentSymbol> Parse(string text, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        token.ThrowIfCancellationRequested();

        // 等长遮蔽注释、字符串及预处理行，保留原文偏移，避免其中的分隔符改变节点层级。
        var visible = MaskTrivia(text, token);
        var positions = new PositionMap(text);
        var roots = new List<MutableSymbol>();
        var frames = new Stack<MutableSymbol?>();
        var segmentStart = 0;
        var hiddenDepth = 0;
        var symbolCount = 0;

        for (var i = 0; i < visible.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            // &{/absolute/path} 是节点引用；其内部花括号不是节点边界。
            if (visible[i] == '&' && i + 1 < visible.Length && visible[i + 1] == '{')
            {
                var referenceEnd = visible.IndexOf('}', i + 2);
                if (referenceEnd >= 0)
                {
                    i = referenceEnd;
                    continue;
                }
            }

            switch (visible[i])
            {
                case '{':
                {
                    if (hiddenDepth > 0 || frames.Count >= MaxDepth)
                    {
                        hiddenDepth++;
                    }
                    else
                    {
                        MutableSymbol? node = null;
                        if ((frames.Count == 0 || frames.Peek() is not null) && symbolCount < MaxSymbols &&
                            TryReadName(visible, segmentStart, i, rejectAssignment: true,
                                out var name, out var nameStart, out var nameEnd))
                        {
                            node = new MutableSymbol(name, name == "/" ? "根节点" : name.StartsWith('&') ? "节点引用" : "节点",
                                NodeKind, nameStart, nameEnd, text.Length);
                            Attach(roots, frames, node);
                            symbolCount++;
                        }
                        frames.Push(node);
                    }
                    segmentStart = i + 1;
                    break;
                }
                case '}':
                {
                    if (hiddenDepth > 0)
                    {
                        hiddenDepth--;
                    }
                    else if (frames.Count > 0)
                    {
                        var node = frames.Pop();
                        if (node is not null)
                        {
                            // DTS 节点通常以 }; 结束；范围同时覆盖结尾分号。
                            var end = i + 1;
                            while (end < visible.Length && char.IsWhiteSpace(visible[end]))
                            {
                                end++;
                            }
                            node.End = end < visible.Length && visible[end] == ';' ? end + 1 : i + 1;
                        }
                    }
                    segmentStart = i + 1;
                    break;
                }
                case ';':
                {
                    if (hiddenDepth == 0 && (frames.Count == 0 || frames.Peek() is not null) &&
                        symbolCount < MaxSymbols &&
                        TryReadProperty(visible, segmentStart, i, out var name, out var nameStart,
                            out var nameEnd, out var detail))
                    {
                        var property = new MutableSymbol(name, detail, PropertyKind, nameStart, nameEnd, i + 1);
                        Attach(roots, frames, property);
                        symbolCount++;
                    }
                    segmentStart = i + 1;
                    break;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        // 编辑中的未闭合节点仍可导航，但范围只延伸到当前文档末尾。
        return roots.Select(root => root.ToDocumentSymbol(positions)).ToArray();
    }

    private static void Attach(List<MutableSymbol> roots, Stack<MutableSymbol?> frames, MutableSymbol symbol)
    {
        if (frames.Count == 0)
        {
            roots.Add(symbol);
        }
        else
        {
            frames.Peek()?.Children.Add(symbol);
        }
    }

    private static bool TryReadProperty(string visible, int start, int end, out string name,
        out int nameStart, out int nameEnd, out string detail)
    {
        name = string.Empty;
        nameStart = nameEnd = 0;
        detail = "属性";
        var first = start;
        while (first < end && char.IsWhiteSpace(visible[first]))
        {
            first++;
        }
        if (first == end || visible.AsSpan(first, end - first).StartsWith("/dts-v1/", StringComparison.Ordinal) ||
            visible.AsSpan(first, end - first).StartsWith("/plugin/", StringComparison.Ordinal) ||
            visible.AsSpan(first, end - first).StartsWith("/memreserve/", StringComparison.Ordinal) ||
            visible.AsSpan(first, end - first).StartsWith("/delete-node/", StringComparison.Ordinal))
        {
            return false;
        }
        if (visible.AsSpan(first, end - first).StartsWith("/delete-property/", StringComparison.Ordinal))
        {
            first += "/delete-property/".Length;
            detail = "删除属性";
        }
        else if (visible.AsSpan(first, end - first).StartsWith("/include/", StringComparison.Ordinal))
        {
            return false;
        }

        var assignment = visible.IndexOf('=', first, end - first);
        var declarationEnd = assignment >= 0 ? assignment : end;
        if (!TryReadName(visible, first, declarationEnd, rejectAssignment: false,
                out name, out nameStart, out nameEnd))
        {
            return false;
        }
        if (assignment < 0 && detail == "属性")
        {
            detail = "布尔属性";
        }
        return true;
    }

    private static bool TryReadName(string visible, int start, int end, bool rejectAssignment,
        out string name, out int nameStart, out int nameEnd)
    {
        name = string.Empty;
        nameStart = nameEnd = 0;
        if (rejectAssignment && visible.IndexOf('=', start, end - start) >= 0)
        {
            return false;
        }
        while (end > start && char.IsWhiteSpace(visible[end - 1]))
        {
            end--;
        }
        if (end == start)
        {
            return false;
        }
        var tokenStart = end;
        while (tokenStart > start && !char.IsWhiteSpace(visible[tokenStart - 1]))
        {
            tokenStart--;
        }
        // 标签可以紧挨名称书写，如 led0:led_0；导航选择范围只指向节点或属性名。
        var colon = visible.LastIndexOf(':', end - 1, end - tokenStart);
        if (colon >= tokenStart)
        {
            tokenStart = colon + 1;
        }
        if (tokenStart == end)
        {
            return false;
        }
        var candidate = visible[tokenStart..end];
        if (candidate is "/delete-node/" or "/delete-property/" or "/omit-if-no-ref/" ||
            candidate.Any(ch => !IsNameCharacter(ch)))
        {
            return false;
        }
        name = candidate;
        nameStart = tokenStart;
        nameEnd = end;
        return true;
    }

    private static bool IsNameCharacter(char character) => char.IsLetterOrDigit(character) ||
        character is '_' or '-' or '+' or '.' or ',' or '@' or '/' or '#' or '&' or '{' or '}' or '?' or '$';

    private static string MaskTrivia(string text, CancellationToken token)
    {
        var masked = text.ToCharArray();
        var atLineStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            if (text[i] == '#' && atLineStart && IsPreprocessorDirective(text, i))
            {
                i = MaskPreprocessorLine(text, masked, i);
                atLineStart = true;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i + 2);
                if (end < 0)
                {
                    end = text.Length;
                }
                Mask(masked, text, i, end);
                i = end - 1;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var closing = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = closing < 0 ? text.Length : closing + 2;
                Mask(masked, text, i, end);
                if (text.AsSpan(i, end - i).Contains('\n'))
                {
                    atLineStart = true;
                }
                i = end - 1;
                continue;
            }
            if (text[i] == '"')
            {
                var end = i + 1;
                while (end < text.Length)
                {
                    if (text[end] == '\\' && end + 1 < text.Length)
                    {
                        end += 2;
                    }
                    else if (text[end++] == '"')
                    {
                        break;
                    }
                }
                Mask(masked, text, i, end);
                atLineStart = false;
                i = end - 1;
                continue;
            }
            if (text[i] == '\n')
            {
                atLineStart = true;
            }
            else if (!char.IsWhiteSpace(text[i]))
            {
                atLineStart = false;
            }
        }
        return new string(masked);
    }

    private static bool IsPreprocessorDirective(string text, int hash)
    {
        var start = hash + 1;
        while (start < text.Length && text[start] is ' ' or '\t')
        {
            start++;
        }
        if (start < text.Length && char.IsDigit(text[start]))
        {
            return true;
        }
        var end = start;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }
        return text[start..end] is "include" or "define" or "undef" or "if" or "ifdef" or "ifndef" or
            "elif" or "else" or "endif" or "pragma" or "error" or "warning" or "line";
    }

    private static int MaskPreprocessorLine(string text, char[] masked, int start)
    {
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                Mask(masked, text, start, text.Length);
                return text.Length - 1;
            }
            var last = end - 1;
            while (last >= start && text[last] is ' ' or '\t' or '\r')
            {
                last--;
            }
            Mask(masked, text, start, end);
            if (last < start || text[last] != '\\')
            {
                return end;
            }
            start = end + 1;
        }
        return text.Length - 1;
    }

    private static void Mask(char[] masked, string original, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (original[i] != '\n')
            {
                masked[i] = ' ';
            }
        }
    }

    private sealed class MutableSymbol(string name, string detail, int kind, int selectionStart, int selectionEnd, int end)
    {
        public List<MutableSymbol> Children { get; } = [];
        public int End { get; set; } = end;

        public CodeDocumentSymbol ToDocumentSymbol(PositionMap positions) =>
            new(name, detail, kind,
                new CodeRange(positions.FromOffset(selectionStart), positions.FromOffset(End)),
                new CodeRange(positions.FromOffset(selectionStart), positions.FromOffset(selectionEnd)),
                Children.Select(child => child.ToDocumentSymbol(positions)).ToArray());
    }

    private sealed class PositionMap
    {
        private readonly int[] starts;

        public PositionMap(string text)
        {
            var lines = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines.Add(i + 1);
                }
            }
            starts = [.. lines];
        }

        public CodePosition FromOffset(int offset)
        {
            var line = Array.BinarySearch(starts, offset);
            if (line < 0)
            {
                line = ~line - 1;
            }
            return new CodePosition(line, offset - starts[line]);
        }
    }
}
