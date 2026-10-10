namespace StudioX.Application.BuildConfiguration;

using StudioX.Foundation;

/// <summary>保存完整字符范围，不求值 CMake；注释与字符串中的命令不参与源码登记。</summary>
internal static class LiteralCMakeSyntax
{
    internal sealed record Argument(string Value, int Start, int End, bool Escaped = false);
    internal sealed record Call(string Name, int Start, int Close, int Depth, IReadOnlyList<Argument> Arguments);

    public static IReadOnlyList<Call> Read(string text)
    {
        if (text.Length > 1024 * 1024)
        {
            throw Invalid("构建文件超过 1 Mi 字符");
        }
        var calls = new List<Call>();
        var blocks = new Stack<string>();
        var at = 0;
        while (true)
        {
            Skip();
            if (at == text.Length)
            {
                break;
            }
            var start = at;
            if (!char.IsAsciiLetter(text[at]) && text[at] != '_')
            {
                throw Invalid("命令名称无效");
            }
            while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_'))
            {
                at++;
            }
            if (at == start)
            {
                throw Invalid("存在无法识别的命令");
            }
            var name = text[start..at].ToLowerInvariant();
            Skip();
            if (at == text.Length || text[at++] != '(')
            {
                throw Invalid("命令括号不完整");
            }
            var arguments = new List<Argument>();
            var nested = 0;
            while (true)
            {
                Skip();
                if (at == text.Length)
                {
                    throw Invalid("命令未闭合");
                }
                if (text[at] == ')' && nested == 0)
                {
                    break;
                }
                var begin = at;
                var escaped = false;
                string value;
                if (text[at] is '(' or ')')
                {
                    nested += text[at] == '(' ? 1 : -1;
                    value = text[at++].ToString();
                    escaped = true;
                }
                else if (text[at] == '"')
                {
                    var content = ++at;
                    while (at < text.Length && text[at] != '"')
                    {
                        if (text[at] == '\\' && at + 1 < text.Length)
                        {
                            escaped = true;
                            at += 2;
                        }
                        else
                        {
                            at++;
                        }
                    }
                    if (at == text.Length)
                    {
                        throw Invalid("引号字符串未闭合");
                    }
                    value = text[content..at++];
                }
                else if (Bracket(at, out var content, out var closing))
                {
                    var end = text.IndexOf(closing, content, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Invalid("括号字符串未闭合");
                    }
                    if (text.AsSpan(content).StartsWith("\r\n"))
                    {
                        content += 2;
                    }
                    else if (content < text.Length && text[content] == '\n')
                    {
                        content++;
                    }
                    value = text[content..end];
                    at = end + closing.Length;
                }
                else
                {
                    while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not '(' and not ')' and not '#')
                    {
                        if (text[at] == '\\' && at + 1 < text.Length)
                        {
                            escaped = true;
                            at += 2;
                        }
                        else
                        {
                            at++;
                        }
                    }
                    value = text[begin..at];
                }
                arguments.Add(new(value, begin, at, escaped));
                if (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ')' and not '(' and not '#')
                {
                    throw Invalid("参数之间缺少明确分隔");
                }
            }
            calls.Add(new(name, start, at++, blocks.Count, arguments));
            if (name is "if" or "foreach" or "while" or "function" or "macro" or "block")
            {
                blocks.Push(name);
            }
            else if (name is "endif" or "endforeach" or "endwhile" or "endfunction" or "endmacro" or "endblock")
            {
                if (!blocks.TryPop(out var opening) || name != "end" + opening)
                {
                    throw Invalid("条件或函数块不匹配");
                }
            }
        }
        if (blocks.Count != 0)
        {
            throw Invalid("条件或函数块未闭合");
        }
        return calls;

        void Skip()
        {
            while (at < text.Length)
            {
                if (char.IsWhiteSpace(text[at]) || text[at] == '\ufeff')
                {
                    at++;
                    continue;
                }
                if (text[at] != '#')
                {
                    break;
                }
                if (Bracket(at + 1, out var content, out var closing))
                {
                    var end = text.IndexOf(closing, content, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Invalid("括号注释未闭合");
                    }
                    at = end + closing.Length;
                }
                else
                {
                    while (at < text.Length && text[at] != '\n')
                    {
                        at++;
                    }
                }
            }
        }
        bool Bracket(int offset, out int content, out string closing)
        {
            content = offset;
            closing = "";
            if (offset >= text.Length || text[offset] != '[')
            {
                return false;
            }
            var next = offset + 1;
            while (next < text.Length && text[next] == '=')
            {
                next++;
            }
            if (next == text.Length || text[next] != '[')
            {
                return false;
            }
            content = next + 1;
            closing = "]" + new string('=', next - offset - 1) + "]";
            return true;
        }
    }
    private static StudioXException Invalid(string reason) => new("SOURCE_CMAKE_SYNTAX", "无法读取源码登记：" + reason + "。请核对 CMakeLists.txt；未修改工程。");
}
