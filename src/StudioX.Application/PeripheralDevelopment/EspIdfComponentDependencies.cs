namespace StudioX.Application.PeripheralDevelopment;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>只修改可证明归属的字面量组件注册；保留其余文本，不求值或展开用户 CMake。</summary>
internal static class EspIdfComponentDependencies
{
    private sealed record Argument(string Value, int End);
    private sealed record Call(string Name, int Close, List<Argument> Arguments, int Depth);
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "SRCS", "SRC_DIRS", "EXCLUDE_SRCS", "INCLUDE_DIRS", "PRIV_INCLUDE_DIRS", "LDFRAGMENTS", "REQUIRES",
        "PRIV_REQUIRES", "REQUIRED_IDF_TARGETS", "EMBED_FILES", "EMBED_TXTFILES", "KCONFIG", "KCONFIG_PROJBUILD", "WHOLE_ARCHIVE"
    };

    public static (string Text, string[] Added) Prepare(string text, string source, IReadOnlyList<string> required)
    {
        if (text.Length > 1024 * 1024)
        {
            throw Unsupported("组件配置超过读取范围");
        }
        var calls = Parse(text);
        if (calls.Any(call => call.Name is "function" or "macro" && call.Arguments.FirstOrDefault()?.Value.Equals("idf_component_register", StringComparison.OrdinalIgnoreCase) == true))
        {
            throw Unsupported("组件重定义了 idf_component_register");
        }
        var registrations = calls.Where(call => call.Name == "idf_component_register").ToArray();
        if (registrations.Length != 1 || registrations[0].Depth != 0)
        {
            throw Unsupported("需要一个位于文件顶层的 idf_component_register；当前注册入口缺失、重复或位于条件/函数中");
        }
        var registration = registrations[0];
        var sections = new Dictionary<string, List<Argument>>(StringComparer.Ordinal);
        string? section = null;
        foreach (var argument in registration.Arguments)
        {
            if (Keywords.Contains(argument.Value))
            {
                section = argument.Value;
                if (!sections.TryAdd(section, []))
                {
                    throw Unsupported("注册参数 " + section + " 重复出现");
                }
            }
            else if (section is null || section == "WHOLE_ARCHIVE")
            {
                throw Unsupported("存在无法归属的注册参数");
            }
            else
            {
                sections[section].Add(argument);
            }
        }
        string[] Values(string name) => sections.GetValueOrDefault(name)?.SelectMany(argument => argument.Value.Split(';', StringSplitOptions.RemoveEmptyEntries)).ToArray() ?? [];
        var sources = Values("SRCS");
        var directories = Values("SRC_DIRS");
        var excludes = Values("EXCLUDE_SRCS");
        var dependencies = Values("REQUIRES").Concat(Values("PRIV_REQUIRES")).ToArray();
        if (sources.Concat(directories).Concat(excludes).Concat(dependencies).Any(value => value.Contains('$') || value.Contains('\\')))
        {
            throw Unsupported("源码列表或依赖使用了变量、生成表达式或转义路径");
        }
        var normalizedSource = Normalize(source);
        // IDF 有 SRC_DIRS 时忽略 SRCS；目录收集仅覆盖本层文件，不推断递归或外部源码。
        var registered = directories.Length > 0
            ? directories.Any(directory => Normalize(directory) == Normalize(Path.GetDirectoryName(source)?.Replace('\\', '/') ?? "."))
            : sources.Any(item => Normalize(item) == normalizedSource);
        if (!registered || excludes.Any(item => Normalize(item) == normalizedSource))
        {
            throw Unsupported("当前 C 源文件未明确登记到此组件，或已被 EXCLUDE_SRCS 排除");
        }
        if (dependencies.Concat(required).Any(value => !Regex.IsMatch(value, "^[A-Za-z0-9_][A-Za-z0-9_.+-]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))))
        {
            throw Unsupported("组件依赖包含无法可靠识别的名称");
        }
        var missing = required.Distinct(StringComparer.Ordinal).Except(dependencies, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
        {
            return (text, []);
        }
        if (sections.TryGetValue("PRIV_REQUIRES", out var privateDependencies))
        {
            var last = privateDependencies.LastOrDefault() ?? registration.Arguments.Single(argument => argument.Value == "PRIV_REQUIRES");
            return (text.Insert(last.End, " " + string.Join(' ', missing)), missing);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return (text.Insert(registration.Close, newline + "    PRIV_REQUIRES " + string.Join(' ', missing) + newline), missing);
    }

    private static string Normalize(string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/').Contains(".."))
        {
            throw Unsupported("源码路径使用了绝对路径或上级目录");
        }
        return string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != "."));
    }

    private static List<Call> Parse(string text)
    {
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
            while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_'))
            {
                at++;
            }
            if (at == start)
            {
                throw Unsupported("存在无法识别的 CMake 命令");
            }
            var name = text[start..at].ToLowerInvariant();
            Skip();
            if (at == text.Length || text[at++] != '(')
            {
                throw Unsupported("CMake 命令括号不完整");
            }
            var arguments = new List<Argument>();
            while (true)
            {
                Skip();
                if (at == text.Length)
                {
                    throw Unsupported("CMake 命令未闭合");
                }
                if (text[at] == ')')
                {
                    break;
                }
                start = at;
                string value;
                if (text[at] == '"')
                {
                    at++;
                    start = at;
                    while (at < text.Length && text[at] != '"')
                    {
                        // 其他命令可带转义，但注册列表遇到转义时由 Prepare 拒绝，避免改变参数语义。
                        if (text[at] == '\\' && at + 1 < text.Length)
                        {
                            at += 2;
                        }
                        else
                        {
                            at++;
                        }
                    }
                    if (at == text.Length)
                    {
                        throw Unsupported("CMake 字符串未闭合");
                    }
                    value = text[start..at++];
                }
                else if (Bracket(at, out var content, out var closing))
                {
                    var end = text.IndexOf(closing, content, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        throw Unsupported("CMake 括号字符串未闭合");
                    }
                    value = text[content..end];
                    at = end + closing.Length;
                }
                else
                {
                    while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not ')' and not '#')
                    {
                        if (text[at] == '(')
                        {
                            throw Unsupported("CMake 参数含嵌套括号");
                        }
                        if (text[at] == '\\' && at + 1 < text.Length)
                        {
                            at += 2;
                        }
                        else
                        {
                            at++;
                        }
                    }
                    value = text[start..at];
                }
                arguments.Add(new(value, at));
            }
            calls.Add(new(name, at++, arguments, blocks.Count));
            if (name is "if" or "foreach" or "while" or "function" or "macro" or "block")
            {
                blocks.Push(name);
            }
            else if (name is "endif" or "endforeach" or "endwhile" or "endfunction" or "endmacro" or "endblock")
            {
                if (!blocks.TryPop(out var opening) || name != "end" + opening)
                {
                    throw Unsupported("CMake 条件或函数块不完整");
                }
            }
        }
        if (blocks.Count != 0)
        {
            throw Unsupported("CMake 条件或函数块未闭合");
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
                        throw Unsupported("CMake 括号注释未闭合");
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

    private static StudioXException Unsupported(string reason) => new("PERIPHERAL_CMAKE_UNSUPPORTED",
        "无法自动补齐组件依赖：" + reason + "。请打开当前组件的 CMakeLists.txt，核对 SRCS/SRC_DIRS 与 REQUIRES/PRIV_REQUIRES；可展开详情复制所需依赖。未修改工程。");
}
