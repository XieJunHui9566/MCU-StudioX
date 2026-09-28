namespace StudioX.Application.CodeIntelligence;

using System.Globalization;

/// <summary>容忍未完成输入的词法扫描；字符串整体隔离，避免把文本内容作为符号或调用。</summary>
internal sealed class PythonSyntax
{
    internal sealed record Token(string Value, int Start, int End, bool Name = false);
    internal List<Token> Tokens { get; } = [];
    internal bool Suppressed
    {
        get; private set;
    }

    internal static bool IsNameStart(char value) => value == '_' || char.IsLetter(value);
    internal static bool IsNamePart(char value) => IsNameStart(value) || char.IsDigit(value) ||
        char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    internal static PythonSyntax Scan(string text, int caret, CancellationToken cancellationToken)
    {
        var syntax = new PythonSyntax();
        var i = 0;
        while (i < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = i;
            var c = text[i];
            if (c == '#')
            {
                while (i < text.Length && text[i] is not '\r' and not '\n')
                {
                    i++;
                }
                syntax.Suppressed |= caret > start && caret <= i;
            }
            else if (TryString(text, i, out var quote, out var formatted))
            {
                i = ReadString(text, quote, formatted, 0, cancellationToken, out var closed);
                syntax.Suppressed |= caret > start && (caret < i || !closed && caret == i);
                syntax.Tokens.Add(new("<string>", start, i));
            }
            else if (IsNameStart(c))
            {
                while (++i < text.Length && IsNamePart(text[i]))
                {
                }
                syntax.Tokens.Add(new(text[start..i], start, i, true));
            }
            else if (char.IsDigit(c))
            {
                // 数值中的字母和下划线不可成为变量；指数符号无需参与补全。
                while (++i < text.Length && (IsNamePart(text[i]) || text[i] == '.'))
                {
                }
                syntax.Tokens.Add(new("<number>", start, i));
            }
            else if (c == '\r' || c == '\n')
            {
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                syntax.Tokens.Add(new("\n", start, i));
            }
            else
            {
                i++;
                if (!char.IsWhiteSpace(c))
                {
                    syntax.Tokens.Add(new(c.ToString(), start, i));
                }
            }
        }
        return syntax;
    }

    private static bool TryString(string text, int start, out int quote, out bool formatted)
    {
        quote = start;
        formatted = false;
        if (text[start] is '\'' or '"')
        {
            return true;
        }
        while (quote < text.Length && quote - start < 2 && "rRbBuUfFtT".Contains(text[quote]))
        {
            quote++;
        }
        if (quote == start || quote >= text.Length || text[quote] is not ('\'' or '"'))
        {
            return false;
        }
        var prefix = text[start..quote].ToLowerInvariant();
        formatted = prefix.Contains('f') || prefix.Contains('t');
        return prefix is "r" or "u" or "b" or "f" or "t" or "br" or "rb" or "fr" or "rf" or "tr" or "rt";
    }

    private static int ReadString(string text, int quote, bool formatted, int depth,
        CancellationToken cancellationToken, out bool closed)
    {
        closed = false;
        if (depth > 32)
        {
            // 极端嵌套按未完成字符串隔离，避免编辑输入耗尽线程栈。
            return text.Length;
        }
        var delimiter = text[quote];
        var triple = quote + 2 < text.Length && text[quote + 1] == delimiter && text[quote + 2] == delimiter;
        var i = quote + (triple ? 3 : 1);
        var braces = 0;
        while (i < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var c = text[i];
            if (formatted && braces > 0)
            {
                if (TryString(text, i, out var nestedQuote, out var nestedFormatted))
                {
                    i = ReadString(text, nestedQuote, nestedFormatted, depth + 1, cancellationToken, out _);
                    continue;
                }
                if (c == '#')
                {
                    while (i < text.Length && text[i] is not '\r' and not '\n')
                    {
                        i++;
                    }
                    continue;
                }
                if (c == '{')
                {
                    braces++;
                }
                if (c == '}')
                {
                    braces--;
                }
                i++;
                continue;
            }
            if (c == '\\')
            {
                i = Math.Min(text.Length, i + (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n' ? 3 : 2));
                continue;
            }
            if (formatted && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    i += 2;
                }
                else
                {
                    braces = 1;
                    i++;
                }
                continue;
            }
            if (c == delimiter && (!triple || i + 2 < text.Length && text[i + 1] == delimiter && text[i + 2] == delimiter))
            {
                closed = true;
                return i + (triple ? 3 : 1);
            }
            if (!triple && c is '\r' or '\n')
            {
                return i;
            }
            i++;
        }
        return i;
    }
}
