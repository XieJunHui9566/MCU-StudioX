namespace StudioX.Application.CodeIntelligence;

/// <summary>从编辑缓冲区提供 Python 提示，不执行源码、不探测或安装 Python 环境。</summary>
public sealed class PythonAssistanceService
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".py" or ".pyw" or ".pyi";

    public Task<PythonAssistance> GetAsync(string path, string text, int offset, CancellationToken cancellationToken = default,
        StudioX.Packages.MicroPythonProfile? microPython = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, text.Length);
        return Task.Run(() => Get(path, text, offset, cancellationToken, microPython), cancellationToken);
    }

    private static PythonAssistance Get(string path, string text, int offset, CancellationToken token, StudioX.Packages.MicroPythonProfile? microPython)
    {
        token.ThrowIfCancellationRequested();
        // 与编辑器大文件保护分开设限，未完成或粘贴的文本也不能无限占用辅助线程。
        if (!Supports(path) || text.Length > 1024 * 1024)
        {
            return new([], null);
        }
        var syntax = PythonSyntax.Scan(text, offset, token);
        if (syntax.Suppressed)
        {
            return new([], null);
        }
        var start = offset;
        var end = offset;
        while (start > 0 && PythonSyntax.IsNamePart(text[start - 1]))
        {
            start--;
        }
        while (end < text.Length && PythonSyntax.IsNamePart(text[end]))
        {
            end++;
        }
        var prefix = text[start..offset];
        var tokens = syntax.Tokens;
        var hints = microPython is null ? null : MicroPythonHints.Get(tokens, text, offset, start, end);
        if (hints?.Suggestions is { } specific)
        {
            return new(specific, hints.Signature);
        }
        if (IsImportContext(tokens, offset))
        {
            return new([], null);
        }
        var before = tokens.FindLastIndex(item => item.End <= start);
        var entries = new Dictionary<string, PythonCatalog.Entry>(StringComparer.Ordinal);
        foreach (var keyword in PythonCatalog.KeywordSet)
        {
            entries[keyword] = new(keyword, 14, "Python 3 关键字", null);
        }
        foreach (var entry in PythonCatalog.Entries)
        {
            entries[entry.Name] = microPython is null ? entry : entry with
            {
                Detail = "MicroPython 常用内置名称（不提供 CPython 参数扩展）",
                Signature = null
            };
        }
        foreach (var keyword in new[] { "match", "case" })
        {
            entries[keyword] = new(keyword, 14, "Python 3 模式匹配软关键字", null);
        }
        foreach (var item in tokens)
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= 4096)
            {
                break;
            }
            if (item.Name && (item.End <= start || item.Start >= end) && !PythonCatalog.KeywordSet.Contains(item.Value))
            {
                entries.TryAdd(item.Value, new(item.Value, 6, "当前文件标识符（词法提示）", null));
            }
        }
        AddDefinitions(text, tokens, entries, offset, token);
        if (hints is not null)
        {
            foreach (var (name, entry) in hints.Names) { entries[name] = entry; }
        }
        var signature = hints?.Signature ?? FindSignature(tokens, offset, entries);
        // 属性名必须有类型或模块证据；不可用无关的全局名称冒充成员分析结果。
        if (before >= 0 && tokens[before].Value == "." || prefix.Length > 0 && !PythonSyntax.IsNameStart(prefix[0]))
        {
            return new([], signature);
        }
        var namesOnly = before >= 0 && tokens[before].Value is "def" or "class" or "as";
        var range = new CodeRange(CodePositions.FromOffset(text, start), CodePositions.FromOffset(text, end));
        var suggestions = entries.Values.Where(entry => entry.Name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(entry => entry.Detail.StartsWith("当前文件", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal).Take(200)
            .Select(entry => new CodeSuggestion(entry.Name, entry.Name, entry.Name,
                entry.Signature?.Label ?? entry.Detail, entry.Detail + (entry.Signature is null ? "" : "\n" + entry.Signature.Documentation),
                namesOnly ? 6 : entry.Kind, range, entry.Name)).ToArray();
        return new(suggestions, signature);
    }

    private static bool IsImportContext(List<PythonSyntax.Token> tokens, int offset)
    {
        // 按逻辑行识别导入，涵盖制表符、括号换行和反斜杠续行；尚未解析模块搜索路径。
        string? first = null;
        var depth = 0;
        var previous = "";
        foreach (var item in tokens)
        {
            if (item.Start >= offset)
            {
                break;
            }
            var value = item.Value;
            if (value == ";" || value == "\n" && depth == 0 && previous != "\\")
            {
                first = null;
            }
            else if (value != "\n")
            {
                first ??= value;
            }
            if (value is "(" or "[" or "{")
            {
                depth++;
            }
            else if (value is ")" or "]" or "}")
            {
                depth = Math.Max(0, depth - 1);
            }
            previous = value;
        }
        return first is "import" or "from";
    }

    private static void AddDefinitions(string text, List<PythonSyntax.Token> tokens,
        Dictionary<string, PythonCatalog.Entry> entries, int offset, CancellationToken cancellationToken)
    {
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tokens[i].Name && tokens[i + 1].Value == "=" && tokens[i + 2].Value != "=" &&
                tokens[i + 1].End <= offset && (i == 0 || tokens[i - 1].Value != "."))
            {
                var variable = tokens[i].Value;
                entries[variable] = new(variable, 6, "当前文件赋值名称（词法提示）", null);
            }
            if (tokens[i].Value is not ("def" or "class") || !tokens[i + 1].Name)
            {
                continue;
            }
            var name = tokens[i + 1].Value;
            if (tokens[i].Value == "class")
            {
                entries[name] = new(name, 7, "当前文件类（词法提示）", null);
                continue;
            }
            if (tokens[i + 2].Value != "(")
            {
                continue;
            }
            var parameters = new List<string>();
            var nesting = 0;
            var parameterStart = tokens[i + 2].End;
            for (var j = i + 3; j < tokens.Count && tokens[j].End - parameterStart < 2048; j++)
            {
                var item = tokens[j];
                if (item.Value == ")" && nesting == 0)
                {
                    AddParameter(item.Start);
                    var label = name + "(" + string.Join(", ", parameters) + ")";
                    entries[name] = new(name, 3, "当前文件函数（词法提示）", new(label,
                        "来自当前编辑缓冲区的函数声明；不进行作用域或类型推断。",
                        parameters.Where(parameter => parameter is not ("/" or "*")).ToArray(), 0));
                    i = j;
                    break;
                }
                if (item.Value == "," && nesting == 0)
                {
                    AddParameter(item.Start);
                    parameterStart = item.End;
                }
                else if (item.Value is "(" or "[" or "{")
                {
                    nesting++;
                }
                else if (item.Value is ")" or "]" or "}")
                {
                    nesting--;
                }
                if (nesting < 0 || parameters.Count > 64)
                {
                    break;
                }
            }
            void AddParameter(int end)
            {
                var value = text[parameterStart..end].Trim();
                if (value.Length > 0)
                {
                    parameters.Add(value);
                }
            }
        }
    }

    private static CodeSignature? FindSignature(List<PythonSyntax.Token> tokens, int offset,
        Dictionary<string, PythonCatalog.Entry> entries)
    {
        var stack = new Stack<(int Open, int Argument, int ArgumentStart)>();
        for (var i = 0; i < tokens.Count && tokens[i].Start < offset; i++)
        {
            var value = tokens[i].Value;
            if (value is "(" or "[" or "{")
            {
                stack.Push((i, 0, i + 1));
            }
            else if (value is ")" or "]" or "}")
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
            }
            else if (value == "," && stack.TryPop(out var frame))
            {
                stack.Push((frame.Open, frame.Argument + 1, i + 1));
            }
            else if (value == "\n" && stack.Count == 0)
            {
                stack.Clear();
            }
        }
        if (!stack.TryPeek(out var call) || tokens[call.Open].Value != "(" || call.Open == 0)
        {
            return null;
        }
        var name = tokens[call.Open - 1];
        if (!name.Name || call.Open >= 2 && tokens[call.Open - 2].Value is "." or "def" or "class" ||
            !entries.TryGetValue(name.Value, out var entry) || entry.Signature is not { } signature)
        {
            return null;
        }
        var parameter = call.Argument;
        var argumentStart = call.ArgumentStart;
        while (argumentStart < tokens.Count && tokens[argumentStart].Value == "\n")
        {
            argumentStart++;
        }
        if (argumentStart + 1 < tokens.Count && tokens[argumentStart].Name &&
            tokens[argumentStart + 1].Value == "=" && tokens[argumentStart + 1].End <= offset)
        {
            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                var parameterName = signature.Parameters[i].Split('=', ':')[0].Trim().TrimStart('*');
                if (parameterName == tokens[argumentStart].Value)
                {
                    parameter = i;
                    break;
                }
            }
        }
        else
        {
            var variadic = signature.Parameters.ToList().FindIndex(value => value.StartsWith('*'));
            if (variadic >= 0)
            {
                parameter = Math.Min(parameter, variadic);
            }
        }
        return signature with
        {
            ActiveParameter = Math.Clamp(parameter, 0, Math.Max(0, signature.Parameters.Count - 1))
        };
    }
}
