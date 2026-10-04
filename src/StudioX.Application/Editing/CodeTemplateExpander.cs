namespace StudioX.Application.Editing;

using System.Text;
using StudioX.Foundation;

/// <summary>有界的纯文本替换器。变量值不会再次解析，正文中的 $$ 表示普通美元符号。</summary>
public static class CodeTemplateExpander
{
    public const int MaximumExpansionLength = 262_144;
    private sealed record Part(string Text, string? Name);
    private sealed record Parsed(IReadOnlyList<Part> Parts, IReadOnlyList<CodeTemplateParameter> Parameters);
    public static IReadOnlyList<CodeTemplateParameter> Describe(string body) => Parse(body).Parameters;

    public static CodeTemplateExpansion Expand(string body, IReadOnlyDictionary<string, string> values, CodeTemplateContext context)
    {
        var parsed = Parse(body);
        var defaults = parsed.Parameters.ToDictionary(p => p.Name, p => p.DefaultValue, StringComparer.Ordinal);
        var text = new StringBuilder();
        int? caret = null;
        foreach (var part in parsed.Parts)
        {
            if (part.Name == "cursor")
            {
                caret = text.Length;
                continue;
            }
            var value = part.Name switch
            {
                null => part.Text,
                "selection" => context.Selection,
                "fileName" => context.FileName,
                "fileStem" => Path.GetFileNameWithoutExtension(context.FileName),
                _ => values.TryGetValue(part.Name, out var supplied) ? supplied : defaults[part.Name]
            };
            if (value is null || value.Length > MaximumExpansionLength || value.Contains('\0'))
            {
                throw Error("模板变量内容无效或过长。");
            }
            if (part.Name is not null)
            {
                if (part.Name != "selection" && value.Any(c => c is '\r' or '\n'))
                {
                    throw Error("填写的模板变量只能是单行文本。");
                }
                if (value.Any(c => c is '\r' or '\n'))
                {
                    var lineStart = text.Length;
                    while (lineStart > 0 && text[lineStart - 1] != '\n')
                    {
                        lineStart--;
                    }
                    var indentEnd = lineStart;
                    while (indentEnd < text.Length && text[indentEnd] is ' ' or '\t')
                    {
                        indentEnd++;
                    }
                    var indent = text.ToString(lineStart, indentEnd - lineStart);
                    value = NormalizeLines(value).Replace("\n", "\n" + indent, StringComparison.Ordinal);
                }
            }
            if (text.Length + value.Length > MaximumExpansionLength)
            {
                throw Error("展开后的模板超过 262,144 字符。");
            }
            text.Append(value);
        }
        return new(text.ToString(), caret ?? text.Length);
    }

    public static CodeTemplateExpansion PrepareInsertion(CodeTemplateExpansion expansion, string document, int offset)
    {
        if (offset < 0 || offset > document.Length || expansion.CaretOffset < 0 || expansion.CaretOffset > expansion.Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        var indent = LineIndent(document, offset);
        var firstNewline = document.IndexOf('\n');
        var newline = firstNewline < 0 ? Environment.NewLine : firstNewline > 0 && document[firstNewline - 1] == '\r' ? "\r\n" : "\n";
        var result = new StringBuilder();
        var caret = 0;
        for (var index = 0; index < expansion.Text.Length; index++)
        {
            if (index == expansion.CaretOffset)
            {
                caret = result.Length;
            }
            var character = expansion.Text[index];
            if (character is '\r' or '\n')
            {
                var caretBetweenNewline = false;
                if (character == '\r' && index + 1 < expansion.Text.Length && expansion.Text[index + 1] == '\n')
                {
                    caretBetweenNewline = expansion.CaretOffset == index + 1;
                    index++;
                }
                result.Append(newline);
                if (index + 1 < expansion.Text.Length && expansion.Text[index + 1] is not '\r' and not '\n')
                {
                    result.Append(indent);
                }
                if (caretBetweenNewline)
                {
                    caret = result.Length;
                }
            }
            else
            {
                result.Append(character);
            }
            if (result.Length > MaximumExpansionLength)
            {
                throw Error("展开后的缩进与换行超过文本上限。");
            }
        }
        if (expansion.CaretOffset == expansion.Text.Length)
        {
            caret = result.Length;
        }
        return new(result.ToString(), caret);
    }

    public static string CaptureSelection(string document, int start, int length, bool escape = true)
    {
        if (start < 0 || length < 0 || start > document.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        var selected = NormalizeLines(document.Substring(start, length));
        var lines = selected.Split('\n');
        var lineStart = start == 0 ? 0 : document.LastIndexOf('\n', start - 1) + 1;
        var indent = LineIndent(document, start);
        if (start == lineStart)
        {
            var populated = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            var common = populated.Length == 0 ? 0 : populated.Min(l => l.TakeWhile(c => c is ' ' or '\t').Count());
            indent = common == 0 ? "" : populated[0][..common];
            while (indent.Length > 0 && !populated.All(l => l.StartsWith(indent, StringComparison.Ordinal)))
            {
                indent = indent[..^1];
            }
        }
        for (var index = start == lineStart ? 0 : 1; index < lines.Length; index++)
        {
            if (lines[index].StartsWith(indent, StringComparison.Ordinal))
            {
                lines[index] = lines[index][indent.Length..];
            }
        }
        var body = string.Join('\n', lines);
        return escape ? body.Replace("$", "$$", StringComparison.Ordinal) : body;
    }

    private static string LineIndent(string document, int offset)
    {
        var lineStart = offset == 0 ? 0 : document.LastIndexOf('\n', offset - 1) + 1;
        return new string(document[lineStart..offset].TakeWhile(c => c is ' ' or '\t').ToArray());
    }
    private static string NormalizeLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static Parsed Parse(string body)
    {
        if (body.Length > CodeTemplateService.MaximumBodyLength || body.Contains('\0'))
        {
            throw Error("模板正文过长或含 NUL。");
        }
        var parts = new List<Part>();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var explicitDefaults = new HashSet<string>(StringComparer.Ordinal);
        var literal = new StringBuilder();
        var cursorSeen = false;
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] != '$')
            {
                literal.Append(body[index]);
                continue;
            }
            if (index + 1 < body.Length && body[index + 1] == '$')
            {
                literal.Append('$');
                index++;
                continue;
            }
            if (index + 1 >= body.Length || body[index + 1] != '{')
            {
                literal.Append('$');
                continue;
            }
            var end = body.IndexOf('}', index + 2);
            if (end < 0)
            {
                throw Error("模板变量缺少 }；普通 ${ 文本请写成 $${。");
            }
            var field = body[(index + 2)..end];
            var separator = field.IndexOf(':');
            var name = separator < 0 ? field : field[..separator];
            var value = separator < 0 ? "" : field[(separator + 1)..];
            if (name.Length is < 1 or > 48 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')) || value.Any(c => c is '\r' or '\n' or '{'))
            {
                throw Error("变量使用 ${name:默认值} 或 ${name}，名称为字母/下划线开头的标识符，默认值须为单行。");
            }
            if (name is "cursor" or "selection" or "fileName" or "fileStem")
            {
                if (separator >= 0 || name == "cursor" && cursorSeen)
                {
                    throw Error("内置变量不能指定默认值；${cursor} 最多出现一次。");
                }
                cursorSeen |= name == "cursor";
            }
            else
            {
                if (explicitDefaults.Contains(name) && separator >= 0 && parameters[name] != value)
                {
                    throw Error("同名变量不能使用不同默认值：" + name);
                }
                if (!parameters.ContainsKey(name) || separator >= 0)
                {
                    parameters[name] = value;
                }
                if (separator >= 0)
                {
                    explicitDefaults.Add(name);
                }
                if (parameters.Count > 32)
                {
                    throw Error("一个模板最多包含 32 个待填写变量。");
                }
            }
            if (literal.Length > 0)
            {
                parts.Add(new(literal.ToString(), null));
                literal.Clear();
            }
            parts.Add(new("", name));
            index = end;
        }
        if (literal.Length > 0)
        {
            parts.Add(new(literal.ToString(), null));
        }
        return new(parts, parameters.Select(p => new CodeTemplateParameter(p.Key, p.Value)).ToArray());
    }
    private static StudioXException Error(string message) => new("CODE_TEMPLATE_SYNTAX", message);
}
