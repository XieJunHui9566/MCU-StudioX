namespace StudioX.Application.CodeIntelligence;

/// <summary>仅向通用 C 分析器投影 SDCC 汇编语句；保留 UTF-16 偏移、换行和宏续行，真实编译仍使用原文。</summary>
internal static class SdccAssemblyProjection
{
    internal static string Create(string text)
    {
        char[]? result = null;
        for (var at = 0; at < text.Length;)
        {
            var start = at;
            var word = Next(text, ref at);
            if (word != "__asm")
            {
                continue;
            }
            var end = at;
            var next = at;
            // GNU asm(...) 不是 SDCC 块；不把无关 C 代码吞进投影。
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }
            if (next < text.Length && text[next] == '(')
            {
                continue;
            }
            var closed = false;
            while (end < text.Length)
            {
                if (text[end] == ';')
                {
                    while (end < text.Length && text[end] is not ('\r' or '\n'))
                    {
                        end++;
                    }
                    continue;
                }
                var token = Next(text, ref end);
                if (token == "__asm")
                {
                    break;
                }
                if (token == "__endasm")
                {
                    closed = true;
                    break;
                }
            }
            if (!closed)
            {
                continue;
            } // 拼错/缺少 __endasm 仍交给分析器报错。
            result ??= text.ToCharArray();
            for (var i = start; i < end; i++)
            {
                if (text[i] is not ('\r' or '\n') && !(text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '\r' or '\n'))
                {
                    result[i] = ' ';
                }
            }
            result[start] = ';';
            at = end;
        }
        return result is null ? text : new string(result);
    }

    private static string Next(string text, ref int at)
    {
        var start = at;
        var c = text[at++];
        if (c is '\'' or '"')
        {
            while (at < text.Length)
            {
                var v = text[at++];
                if (v == '\\' && at < text.Length)
                {
                    at++;
                }
                else if (v == c)
                {
                    break;
                }
            }
        }
        else if (c == '/' && at < text.Length && text[at] is '/' or '*')
        {
            var line = text[at++] == '/';
            while (at < text.Length)
            {
                if (line && text[at] is '\r' or '\n')
                {
                    break;
                }
                if (!line && text[at] == '*' && at + 1 < text.Length && text[at + 1] == '/')
                {
                    at += 2;
                    break;
                }
                at++;
            }
        }
        else if (char.IsLetter(c) || c == '_')
        {
            while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_'))
            {
                at++;
            }
            return text[start..at];
        }
        return "";
    }
}
