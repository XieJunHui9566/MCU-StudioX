namespace StudioX.Application.CodeIntelligence;

public readonly record struct ColoredBracket(int Offset, int Depth);

/// <summary>仅返回已经配对的括号位置，忽略注释与字面量；不修改源文本。</summary>
public static class BracketPairs
{
    public static bool Supports(string language) => language is "C" or "C++" or "CMake" or "JSON" or "Linker" or "Verilog";

    public static ColoredBracket[] Find(string text, string language, CancellationToken token = default)
    {
        if (!Supports(language)) return [];
        var brackets = new List<ColoredBracket>();
        var stack = new List<(char Kind, int Offset, int Depth)>();
        var cmake = language == "CMake";
        var cStyle = language is "C" or "C++" or "Linker" or "Verilog";
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
            var c = text[i];
            if (cStyle && c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i += 2;
                    while (i < text.Length)
                    {
                        if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\r' or '\n')
                        { i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 3 : 2; continue; }
                        if (text[i] is '\r' or '\n') break;
                        i++;
                    }
                    continue;
                }
                if (text[i + 1] == '*')
                { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 1; continue; }
            }
            if (cmake && c is '#' or '[')
            {
                var open = c == '#' ? i + 1 : i;
                if (open < text.Length && text[open] == '[')
                {
                    var end = open + 1;
                    while (end < text.Length && text[end] == '=') end++;
                    if (end < text.Length && text[end] == '[')
                    {
                        var close = "]" + text[(open + 1)..end] + "]";
                        var at = text.IndexOf(close, end + 1, StringComparison.Ordinal);
                        i = at < 0 ? text.Length : at + close.Length - 1; continue;
                    }
                }
                if (c == '#') { var end = text.IndexOf('\n', i); i = end < 0 ? text.Length : end; continue; }
            }
            // C++ 原始字符串可以包含引号、注释符号及任意括号。
            if (language == "C++" && c == 'R' && i + 1 < text.Length && text[i + 1] == '"')
            {
                var open = i + 2;
                while (open < text.Length && open - (i + 2) <= 16 && text[open] != '(' && !char.IsWhiteSpace(text[open]) && text[open] is not ')' and not '\\') open++;
                if (open < text.Length && text[open] == '(' && open - (i + 2) <= 16)
                {
                    var close = ")" + text[(i + 2)..open] + "\"";
                    var at = text.IndexOf(close, open + 1, StringComparison.Ordinal);
                    i = at < 0 ? text.Length : at + close.Length - 1; continue;
                }
            }
            // 整段跳过数值标记，避免把 C++/C23 的数字分隔符当成字符引号。
            if (cStyle && char.IsDigit(c) && (i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')))
            {
                while (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] is '\'' or '.' or '_'))
                { i++; if ((i & 2047) == 0) token.ThrowIfCancellationRequested(); }
                continue;
            }
            // Verilog 的单引号属于位宽常量（如 8'hFF），不能当作字符字面量。
            if (c == '"' || cStyle && language != "Verilog" && c == '\'')
            {
                for (i++; i < text.Length; i++)
                {
                    if ((i & 2047) == 0) token.ThrowIfCancellationRequested();
                    if (text[i] == '\\') { if (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n') i++; i++; continue; }
                    if (text[i] == c) break;
                    if (!cmake && text[i] is '\r' or '\n') break;
                }
                continue;
            }
            if (cmake && c == '\\') { i++; continue; }
            if (c is '(' or '[' or '{') stack.Add((c, i, stack.Count));
            else if (c is ')' or ']' or '}')
            {
                var expected = c switch { ')' => '(', ']' => '[', _ => '{' };
                // 输入尚未完成时丢弃未闭合的内层；不让错误括号把后面的层级全部带偏。
                while (stack.Count > 0 && stack[^1].Kind != expected) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) continue;
                var open = stack[^1]; stack.RemoveAt(stack.Count - 1);
                brackets.Add(new(open.Offset, open.Depth)); brackets.Add(new(i, open.Depth));
            }
        }
        token.ThrowIfCancellationRequested();
        brackets.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        return brackets.ToArray();
    }

}
