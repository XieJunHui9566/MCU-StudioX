namespace StudioX.Application.CodeIntelligence;

using System.Text;

internal sealed record CMakeArgument(string Value, int Start, int End, bool Quoted, bool Closed);
internal sealed record CMakeCall(string Name, List<CMakeArgument> Arguments);
internal sealed record CMakeContext(IReadOnlyList<CMakeCall> Calls, CMakeCall? Call, CMakeArgument? Current,
    bool Suppressed, int ArgumentIndex);

/// <summary>只识别编辑位置与声明，不执行 CMake；引号、转义和任意等号数量的括号注释独立处理。</summary>
internal static class CMakeSyntax
{
    public static CMakeContext Read(string text, int limit, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, text.Length);
        var calls = new List<CMakeCall>();
        CMakeCall? call = null;
        CMakeArgument? current = null;
        string? pending = null;
        var depth = 0;
        var index = 0;
        while (index < limit)
        {
            token.ThrowIfCancellationRequested();
            var c = text[index];
            if (char.IsWhiteSpace(c)) { current = null; index++; continue; }
            if (c == '#')
            {
                if (BracketOpen(text, index + 1, limit, out var begin, out var closing))
                {
                    var end = text.IndexOf(closing, begin, StringComparison.Ordinal);
                    if (end < 0 || end + closing.Length > limit) return Result(true);
                    index = end + closing.Length;
                }
                else
                {
                    var end = text.IndexOf('\n', index);
                    if (end < 0 || end >= limit) return Result(true);
                    index = end + 1;
                }
                current = null; continue;
            }
            if (c == '(')
            {
                if (depth == 0)
                {
                    call = new CMakeCall((pending ?? "").ToLowerInvariant(), []);
                    calls.Add(call); pending = null;
                }
                else call?.Arguments.Add(new("(", index, index + 1, false, true));
                depth++; index++; current = null; continue;
            }
            if (c == ')')
            {
                if (depth > 0 && --depth == 0) call = null;
                else call?.Arguments.Add(new(")", index, index + 1, false, true));
                index++; current = null; pending = null; continue;
            }
            var start = index;
            if (BracketOpen(text, index, limit, out var content, out var close))
            {
                var end = text.IndexOf(close, content, StringComparison.Ordinal);
                if (end < 0 || end + close.Length > limit) return Result(true);
                index = end + close.Length;
                call?.Arguments.Add(new(text[content..end], start, index, true, true));
                current = null; continue;
            }
            var quoted = c == '"';
            if (quoted) index++;
            var value = new StringBuilder();
            var closed = false;
            while (index < limit)
            {
                c = text[index];
                if (c == '\\' && index + 1 < limit)
                {
                    if (text[index + 1] != '\n') value.Append(text[index + 1]);
                    index += 2; continue;
                }
                if (quoted && c == '"') { index++; closed = true; break; }
                if (!quoted && (char.IsWhiteSpace(c) || c is '(' or ')' or '#')) break;
                value.Append(c); index++;
            }
            current = new(value.ToString(), start, index, quoted, closed);
            if (call is not null) call.Arguments.Add(current);
            else pending = current.Value;
            if (closed) current = null;
        }
        return Result(false);

        CMakeContext Result(bool suppressed) => new(calls, call, current, suppressed,
            Math.Max(0, (call?.Arguments.Count ?? 0) - (current is not null && call is not null ? 1 : 0)));
    }

    private static bool BracketOpen(string text, int offset, int limit, out int content, out string close)
    {
        content = offset; close = "";
        if (offset >= limit || text[offset] != '[') return false;
        var end = offset + 1;
        while (end < limit && text[end] == '=') end++;
        if (end >= limit || text[end] != '[') return false;
        content = end + 1; close = "]" + new string('=', end - offset - 1) + "]";
        return true;
    }
}
