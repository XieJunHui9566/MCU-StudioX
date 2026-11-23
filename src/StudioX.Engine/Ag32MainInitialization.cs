using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

namespace StudioX.Engine;

internal static class Ag32MainInitialization
{
    // 分析时屏蔽注释、字面量和宏，同时保留原文位置，生成时只拼接明确的两个范围。
    private static readonly Regex Trivia = new("""//(?:\\\r?\n|[^\r\n])*|/\*[\s\S]*?(?:\*/|\z)|"(?:\\[\s\S]|[^"\\])*"|'(?:\\[\s\S]|[^'\\])*'""");
    private static readonly Regex Directive = new(@"(?m)^[ \t]*#[ \t]*(?<name>\w+)(?:\\\r?\n|[^\r\n])*");
    private static readonly Regex MainBody = new(@"\bmain\s*\([^;{}()]*\)\s*\{");
    private const string Marker = "/* STUDIOX GENERATED ";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private readonly record struct Section(int Start, int End, string Indent = "")
    {
        internal bool Contains(int position) => Start <= position && position < End;
    }

    internal static int FindCallback(string source, string name)
    {
        var chars = source.ToCharArray();
        foreach (Match token in Trivia.Matches(source))
        {
            Blank(chars, token.Index, token.Index + token.Length);
        }
        var masked = new string(chars);
        var code = MaskInactiveCode(masked, Directive.Matches(masked), out _);
        var match = Regex.Match(code, @"\b" + Regex.Escape(name) + @"\s*\([^;{}()]*\)\s*\{");
        return match.Success ? match.Index : -1;
    }

    internal static byte[] Insert(byte[] bytes, Ag32PeripheralModule[] active, Ag32PeripheralModule[] previous)
    {
        Encoding encoding = bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble) ? Encoding.Unicode
            : bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble) ? Encoding.BigEndianUnicode : Utf8;
        var bom = bytes.AsSpan().StartsWith(encoding.Preamble) ? encoding.Preamble.Length : 0;
        if (encoding == Utf8 && bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            bom = 3;
        }
        string text;
        try
        {
            text = encoding.GetString(bytes, bom, bytes.Length - bom);
        }
        catch (DecoderFallbackException)
        {
            // 无 BOM 的本地编码逐字节保留，仅新增 ASCII 注释，避免混入 UTF-8 字节。
            encoding = Encoding.Latin1;
            text = encoding.GetString(bytes);
            bom = 0;
        }

        var commentTokens = Trivia.Matches(text);
        var markers = commentTokens.Where(m => m.Value.StartsWith(Marker, StringComparison.Ordinal)).ToArray();
        var headerLinesOwned = KnownLines(previous.Select(m => $"#include \"{m.Name}.h\""));
        var initLinesOwned = KnownLines(previous.Select(m => m.Init + "();"));
        var existingHeaders = ReadSection(text, markers, "Includes", headerLinesOwned);
        var existingInit = ReadSection(text, markers, "Init", initLinesOwned);
        if (existingHeaders is { } h && existingInit is { } i && h.End > i.Start)
        {
            throw InvalidSections();
        }
        var userMarkers = commentTokens.Where(m => m.Value.StartsWith("/* USER CODE ", StringComparison.Ordinal)).ToArray();
        Match? UserMarker(string kind, string name) => userMarkers.FirstOrDefault(m => m.Value == $"/* USER CODE {kind} {name} */");

        var syntax = text.ToCharArray();
        foreach (Match token in commentTokens)
        {
            Blank(syntax, token.Index, token.Index + token.Length);
        }
        foreach (var existing in new[] { existingHeaders, existingInit }.OfType<Section>())
        {
            if (userMarkers.Any(m => existing.Contains(m.Index)))
            {
                throw InvalidSections();
            }
            // 单独剩下的用户注释不是边界。只有同名且成对的用户区才能确定范围。
            foreach (var begin in userMarkers.Where(m => m.Index < existing.Start && m.Value.StartsWith("/* USER CODE BEGIN ", StringComparison.Ordinal)))
            {
                var close = userMarkers.FirstOrDefault(m => m.Index > begin.Index &&
                    m.Value == begin.Value.Replace("USER CODE BEGIN ", "USER CODE END ", StringComparison.Ordinal));
                if (close is not null && close.Index >= existing.End)
                {
                    throw InvalidSections();
                }
            }
            Blank(syntax, existing.Start, existing.End);
        }
        var masked = new string(syntax);
        var code = MaskInactiveCode(masked, Directive.Matches(masked), out var includes);
        var mains = MainBody.Matches(code);
        if (mains.Count != 1)
        {
            if (markers.Length == 0 && active.Length == 0 && previous.Length == 0)
            {
                return bytes;
            }
            throw new StudioXException("AG32_MAIN_INIT", "无法唯一定位 main 的函数体，请在工程入口提供明确的 main 定义，再生成外设初始化。");
        }
        var open = mains[0].Index + mains[0].Length;
        var end = open;
        var depth = 1;
        while (end < code.Length && depth != 0)
        {
            if (code[end] == '{')
            {
                depth++;
            }
            if (code[end] == '}')
            {
                depth--;
            }
            end++;
        }
        if (depth != 0)
        {
            throw new StudioXException("AG32_MAIN_INIT", "main 函数的大括号不匹配。");
        }
        includes.RemoveAll(d => d.Index >= mains[0].Index || BraceDepth(code.AsSpan(0, d.Index)) != 0);
        var newline = Regex.Match(text, @"\r\n|\n|\r") is { Success: true } first ? first.Value : "\n";
        var inline = false;
        Section headers, init;
        if (existingHeaders is { } foundHeaders)
        {
            headers = foundHeaders;
            if (headers.End > mains[0].Index || BraceDepth(code.AsSpan(0, headers.Start)) != 0)
            {
                throw InvalidSections();
            }
        }
        else if (UserMarker("BEGIN", "Includes") is { } userIncludes && userIncludes.Index < mains[0].Index)
        {
            headers = ReadGeneratedLines(text, LineStart(text, userIncludes.Index), headerLinesOwned, backwards: true);
        }
        else
        {
            headers = LegacyIncludes(text, includes, commentTokens, previous);
            // 两个生成标记都删掉时，旧头文件前可能仍留有生成提示。
            var leading = ReadGeneratedLines(text, headers.Start, headerLinesOwned, backwards: true);
            if (leading.Start != leading.End)
            {
                headers = headers with
                {
                    Start = leading.Start
                };
            }
        }

        if (existingInit is { } foundInit)
        {
            init = foundInit;
            if (init.Start < open || init.End > end - 1 || BraceDepth(code.AsSpan(open, init.Start - open)) != 0)

            {
                throw InvalidSections();
            }
        }
        else
        {
            bool InMain(Match? m) => m is not null && m.Index >= open && m.Index < end - 1 &&
                BraceDepth(code.AsSpan(open, m.Index - open)) == 0;
            var before = UserMarker("END", "BeforeInit");
            var after = UserMarker("BEGIN", "AfterInit");
            var firstLine = Regex.Match(text[open..], @"^[ \t]*(?:\r\n|\n|\r)");
            var start = open + firstLine.Length;
            if (InMain(before))

            {
                init = ReadGeneratedLines(text, AfterLine(text, before!.Index), initLinesOwned);
            }
            else if (InMain(after))

            {
                init = ReadGeneratedLines(text, LineStart(text, after!.Index), initLinesOwned, backwards: true);
            }
            else
            {
                inline = !firstLine.Success;
                init = inline ? new(start, start) : ReadGeneratedLines(text, start, initLinesOwned);
            }
            var indent = Regex.Match(text[start..], @"^[ \t]+(?=\S)").Value;
            init = init with
            {
                Indent = inline || indent.Length == 0 ? "    " : indent
            };
        }
        if (headers.End > init.Start || headers.End > mains[0].Index || init.End > end - 1)
        {
            throw InvalidSections();
        }

        var headerLines = new List<string>();
        var callLines = new List<string>();
        foreach (var module in active)
        {
            var include = new Regex(@"^\s*#\s*include\s*[""<]" + module.Name + @"\.h["">]");
            if (!includes.Any(d => !headers.Contains(d.Index) && include.IsMatch(text.Substring(d.Index, d.Length))))

            {
                headerLines.Add($"#include \"{module.Name}.h\"");
            }
            var calls = Regex.Matches(code[open..(end - 1)], @"\b" + module.Init + @"\s*\(\s*\)\s*;")
                .Count(m => !init.Contains(open + m.Index));
            if (calls == 0)
            {
                callLines.Add(module.Init + "();");
            }
            else if (calls > 1)

            {
                throw new StudioXException("AG32_MAIN_INIT", $"main 中有多处 {module.Init}() 调用，请保留一处后再生成。");
            }
        }
        var hint = encoding == Encoding.Latin1 ? HintAscii : Hint;
        string Generated(string name, Section section, List<string> lines) =>
            string.Join(newline, new[] { $"{Marker}BEGIN {name} */", hint }.Concat(lines).Append($"{Marker}END {name} */")
                .Select(line => section.Indent + line));
        string UserSection(string name, string indent) =>
            $"{indent}/* USER CODE BEGIN {name} */{newline}{newline}{indent}/* USER CODE END {name} */";
        var headerText = Generated("Includes", headers, headerLines);
        var initText = Generated("Init", init, callLines);
        bool NeedsUserSection(string name) => UserMarker("BEGIN", name) is null && UserMarker("END", name) is null;
        if (NeedsUserSection("Includes"))
        {
            headerText += newline + newline + UserSection("Includes", "");
        }
        if (NeedsUserSection("BeforeInit"))
        {
            initText = UserSection("BeforeInit", init.Indent) + newline + newline + initText;
        }
        if (NeedsUserSection("AfterInit"))
        {
            initText += newline + newline + UserSection("AfterInit", init.Indent);
        }
        if (existingHeaders is null)
        {
            headerText += newline;
        }
        if (existingInit is null)
        {
            initText = (inline ? newline : "") + initText + newline + (inline ? init.Indent : "");
        }
        var updated = text[..headers.Start] + headerText + text[headers.End..init.Start] + initText + text[init.End..];
        return updated == text ? bytes : [.. bytes.AsSpan(0, bom), .. encoding.GetBytes(updated)];
    }

    private const string Hint = "/* 图形配置重新生成时会替换本区内容。请将自定义代码写在区外。 */";
    private const string HintAscii = "/* Regeneration replaces this section. Put user code outside it. */";
    private static HashSet<string> KnownLines(IEnumerable<string> lines) => new(lines.Append(Hint).Append(HintAscii), StringComparer.Ordinal);

    private static Section? ReadSection(string text, Match[] markers, string name, HashSet<string> known)
    {
        var begins = markers.Where(m => m.Value == $"{Marker}BEGIN {name} */").ToArray();
        var ends = markers.Where(m => m.Value == $"{Marker}END {name} */").ToArray();
        if (begins.Length == 0 && ends.Length == 0)
        {
            return null;
        }
        if (begins.Length > 1 || ends.Length > 1)
        {
            throw InvalidSections();
        }
        Section MarkerLine(Match m)
        {
            var start = LineStart(text, m.Index);
            var indent = text[start..m.Index];
            var end = m.Index + m.Length;
            if (!indent.All(c => c is ' ' or '\t') || text[end..AfterLine(text, m.Index)].Trim().Length != 0)

            {
                throw InvalidSections();
            }
            return new(start, end, indent);
        }

        if (begins.Length == 0)
        {
            var last = MarkerLine(ends[0]);
            var run = ReadGeneratedLines(text, last.Start, known, backwards: true);
            return last with
            {
                Start = run.Start
            };
        }
        var first = MarkerLine(begins[0]);
        if (ends.Length != 0)
        {
            var last = MarkerLine(ends[0]);
            if (last.Start < first.End)
            {
                throw InvalidSections();
            }
            return first with
            {
                End = last.End
            };
        }
        var following = ReadGeneratedLines(text, AfterLine(text, first.End), known);
        if (following.Start == following.End)
        {
            return first;
        }
        return first with
        {
            End = BeforeNewline(text, following.End)
        };
    }

    // 缺边界时只接管紧邻锚点的、生成器确切认识的整行。用户语句和注释原地保留。
    private static Section ReadGeneratedLines(string text, int anchor, HashSet<string> known, bool backwards = false)
    {
        var run = new Section(anchor, anchor);
        var cursor = anchor;
        while (backwards ? cursor > 0 : cursor < text.Length)
        {
            var start = backwards ? LineStart(text, BeforeNewline(text, cursor)) : cursor;
            var end = backwards ? cursor : AfterLine(text, cursor);
            var line = text[start..end].Trim();
            if (line.Length > 0)
            {
                if (!known.Contains(line))
                {
                    break;
                }
                run = new(Math.Min(run.Start, start), Math.Max(run.End, end));
            }
            cursor = backwards ? start : end;
        }
        return run;
    }

    private static int LineStart(string text, int position) =>
        position == 0 ? 0 : text.LastIndexOfAny(['\r', '\n'], position - 1) + 1;

    private static int BeforeNewline(string text, int end)
    {
        if (end > 0 && text[end - 1] == '\n')
        {
            end--;
        }
        if (end > 0 && text[end - 1] == '\r')
        {
            end--;
        }
        return end;
    }

    private static Section LegacyIncludes(string text, List<Match> includes, MatchCollection comments, Ag32PeripheralModule[] previous)
    {
        // 只接管上版生成器紧邻插入点的独立语句。带注释、移动过或条件编译的用户代码留在原处。
        if (includes.Count == 0)
        {
            return new(0, 0);
        }
        var last = includes[^1];
        var end = AfterLine(text, last.Index + last.Length);
        // 最后一个 include 的行尾块注释可能延续到下一行，此时从该指令前插入。
        if (comments.Any(m => m.Index < end && m.Index + m.Length > end))

        {
            return new(last.Index, last.Index);
        }
        var start = end;
        for (var i = includes.Count - 1; i >= 0; i--)
        {
            var d = includes[i];
            if (AfterLine(text, d.Index + d.Length) != start ||
                !previous.Any(m => text[d.Index..start].Trim() == $"#include \"{m.Name}.h\""))
            {
                break;
            }
            start = d.Index;
        }
        return new(start, end);
    }

    private static void Blank(char[] code, int start, int end)
    {
        for (var i = start; i < end; i++)

        {
            if (code[i] is not ('\r' or '\n'))
            {
                code[i] = ' ';
            }
        }
    }

    private static int AfterLine(string text, int start)
    {
        var end = text.IndexOfAny(['\r', '\n'], start);
        if (end < 0)
        {
            return text.Length;
        }
        return text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? end + 2 : end + 1;
    }

    private static int BraceDepth(ReadOnlySpan<char> code) => code.Count('{') - code.Count('}');

    private static StudioXException InvalidSections() => new("AG32_MAIN_INIT",
        "main 的生成区标记重复、位置异常或与用户区重叠，无法确定要替换的范围。请检查 Includes 和 Init 的位置后重新生成。");

    private static string MaskInactiveCode(string masked, MatchCollection directives, out List<Match> includes)
    {
        includes = [];
        var code = masked.ToCharArray();
        var stack = new Stack<(bool Parent, bool Taken)>();
        var active = true;
        var start = 0;
        foreach (Match d in directives)
        {
            Array.Fill(code, ' ', active ? d.Index : start, d.Index + d.Length - (active ? d.Index : start));
            var name = d.Groups["name"];
            var condition = d.Value[(name.Index + name.Length - d.Index)..].Trim();
            switch (name.Value)
            {
                case "include" when stack.Count == 0:
                    includes.Add(d);
                    break;
                case "if":
                case "ifdef":
                case "ifndef":
                    stack.Push((active, name.Value == "if" && condition == "1"));
                    active &= name.Value != "if" || condition != "0";
                    break;
                case "else" or "elif" when stack.Count != 0:
                    var branch = stack.Pop();
                    active = branch.Parent && !branch.Taken && (name.Value == "else" || condition != "0");
                    stack.Push((branch.Parent, branch.Taken || condition == "1"));
                    break;
                case "endif" when stack.Count != 0:
                    active = stack.Pop().Parent;
                    break;
            }
            start = d.Index + d.Length;
        }
        if (!active)
        {
            Array.Fill(code, ' ', start, code.Length - start);
        }
        return new string(code);
    }
}
