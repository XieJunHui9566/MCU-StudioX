namespace StudioX.Application.BuildConfiguration;

using System.Text.RegularExpressions;
using StudioX.Foundation;
using static LiteralCMakeSyntax;

/// <summary>只改选定目标的字面量源码参数；不展开变量、条件、生成表达式或目录收集。</summary>
internal sealed class SourceListEditor
{
    private sealed record Target(string Name, List<Argument> Sources, Call? AppendCall, int? InsertBefore);
    private static readonly HashSet<string> IdfKeywords = new(StringComparer.Ordinal)
    { "SRCS", "SRC_DIRS", "EXCLUDE_SRCS", "INCLUDE_DIRS", "PRIV_INCLUDE_DIRS", "REQUIRES", "PRIV_REQUIRES", "LDFRAGMENTS", "EMBED_FILES", "EMBED_TXTFILES", "REQUIRED_IDF_TARGETS", "KCONFIG", "KCONFIG_PROJBUILD", "WHOLE_ARCHIVE" };
    private readonly string text;
    private readonly bool idf;
    private readonly List<Target> targets = [];
    public SourceListEditor(string text, bool idf)
    {
        this.text = text;
        this.idf = idf;
        var calls = Read(text);
        string[] commands = idf ? ["idf_component_register"] : ["target_sources", "add_executable", "add_library"];
        if (calls.Any(call => call.Name is "function" or "macro" && call.Arguments.FirstOrDefault() is { } argument && commands.Contains(argument.Value, StringComparer.OrdinalIgnoreCase)))
        {
            throw Unsupported("源码登记命令被重定义");
        }
        if (calls.Any(call => commands.Contains(call.Name) && call.Depth != 0))
        {
            throw Unsupported("源码登记位于条件、循环、函数或宏中");
        }
        if (idf)
        {
            ReadIdf(calls);
        }
        else
        {
            ReadNative(calls);
        }
        foreach (var argument in targets.SelectMany(target => target.Sources))
        {
            if (argument.Escaped || argument.Value.IndexOfAny(['$', '\\', '"', '*', '?', '\r', '\n']) >= 0 || argument.Value.Split(';').Any(value => value.Length == 0))
            {
                throw Unsupported("源码列表使用了变量、生成表达式、转义、通配符或空列表项");
            }
        }
        if (targets.Count == 0)
        {
            throw Unsupported("此文件没有可编辑的字面量目标");
        }
    }
    public IReadOnlyList<SourceRegistrationTarget> Describe(Func<string, string> normalize) => targets.Select(target =>
        new SourceRegistrationTarget(target.Name, target.Sources.SelectMany(argument => argument.Value.Split(';')).Select(normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();

    public string Change(string targetName, IReadOnlyList<SourceRegistrationOperation> operations, Func<string, string> normalize, Func<string, string> relative)
    {
        var target = targets.SingleOrDefault(target => target.Name == targetName) ?? throw Unsupported("请选择此构建文件中明确的目标");
        var replacements = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        var existing = target.Sources.SelectMany(argument => argument.Value.Split(';')).Select(normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (operations.GroupBy(operation => operation.Path, StringComparer.OrdinalIgnoreCase).Any(group => group.Select(operation => operation.Kind).Distinct().Count() > 1))
        {
            throw Unsupported("同一路径不能同时加入、移除或改名");
        }
        foreach (var operation in operations)
        {
            if (!Enum.IsDefined(operation.Kind))
            {
                throw Unsupported("登记操作不受支持");
            }
            if (operation.Kind == SourceRegistrationKind.Add)
            {
                if (existing.Contains(operation.Path) || added.Contains(operation.Path, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                added.Add(operation.Path);
            }
            else
            {
                if (!existing.Contains(operation.Path) || !replacements.TryAdd(operation.Path, operation.Kind == SourceRegistrationKind.Rename ? operation.NewPath : null))
                {
                    throw Unsupported("待移除或改名的路径未唯一选中：" + operation.Path);
                }
            }
        }
        var final = existing.Select(path => replacements.TryGetValue(path, out var changed) ? changed : path).Where(path => path is not null).Concat(added).ToArray();
        if (final.Distinct(StringComparer.OrdinalIgnoreCase).Count() != final.Length)
        {
            throw Unsupported("改名后的路径已经登记或存在重复修改");
        }
        var edits = new List<(int Start, int Length, string Value)>();
        foreach (var argument in target.Sources)
        {
            var values = argument.Value.Split(';');
            if (!values.Any(value => replacements.ContainsKey(normalize(value))))
            {
                continue;
            }
            var next = values.Select(value => replacements.TryGetValue(normalize(value), out var replacement) ? replacement is null ? null : relative(replacement) : value).Where(value => value is not null).ToArray();
            edits.Add((argument.Start, argument.End - argument.Start, next.Length == 0 ? "" : Quote(string.Join(';', next))));
        }
        if (added.Count > 0)
        {
            var values = added.Select(path => Quote(relative(path))).ToArray();
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            if (target.AppendCall is null)
            {
                edits.Add((text.Length, 0, newline + "target_sources(" + target.Name + " PRIVATE " + string.Join(' ', values) + ")" + newline));
            }
            else
            {
                var offset = target.InsertBefore ?? target.AppendCall.Close;
                var prefix = idf ? idfHasSourcesKeyword ? "" : "SRCS " : "PRIVATE ";
                var lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
                var whitespace = text[lineStart..offset];
                var inserted = whitespace.All(char.IsWhiteSpace)
                    ? (offset == target.AppendCall.Close ? "    " : "") + prefix + string.Join(' ', values) + newline + whitespace
                    : " " + prefix + string.Join(' ', values) + " ";
                edits.Add((offset, 0, inserted));
            }
        }
        var result = text;
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            result = result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        }
        return result;
    }
    private void ReadNative(IReadOnlyList<Call> calls)
    {
        foreach (var call in calls.Where(call => call.Name is "add_executable" or "add_library"))
        {
            if (call.Arguments.Count == 0)
            {
                throw Unsupported("目标名称缺失");
            }
            var name = Name(call.Arguments[0]);
            if (call.Arguments.Skip(1).Any(argument => argument.Value is "IMPORTED" or "ALIAS" or "INTERFACE"))
            {
                continue;
            }
            if (targets.Any(target => target.Name == name))
            {
                throw Unsupported("目标被重复声明：" + name);
            }
            string[] flags = ["WIN32", "MACOSX_BUNDLE", "EXCLUDE_FROM_ALL", "STATIC", "SHARED", "MODULE", "OBJECT"];
            targets.Add(new(name, call.Arguments.Skip(1).SkipWhile(argument => flags.Contains(argument.Value)).ToList(), null, null));
        }
        foreach (var call in calls.Where(call => call.Name == "target_sources"))
        {
            if (call.Arguments.Count < 2)
            {
                throw Unsupported("target_sources 缺少目标或范围");
            }
            var name = Name(call.Arguments[0]);
            var index = targets.FindIndex(target => target.Name == name);
            if (index < 0)
            {
                throw Unsupported("target_sources 的目标未在本文件顶层声明：" + name);
            }
            var scope = false;
            foreach (var argument in call.Arguments.Skip(1))
            {
                if (argument.Value is "FILE_SET" or "INTERFACE")
                {
                    throw Unsupported("FILE_SET 或 INTERFACE 源码需要手工维护");
                }
                if (argument.Value is "PRIVATE" or "PUBLIC")
                {
                    scope = true;
                    continue;
                }
                if (!scope)
                {
                    throw Unsupported("源码范围不明确");
                }
                targets[index].Sources.Add(argument);
            }
            targets[index] = targets[index] with
            {
                AppendCall = call
            };
        }
        if (calls.Any(call => call.Name is "set_property" or "set_target_properties" && call.Arguments.Any(argument => argument.Value is "SOURCES" or "INTERFACE_SOURCES")))
        {
            throw Unsupported("目标还通过属性修改源码列表");
        }
    }
    private void ReadIdf(IReadOnlyList<Call> calls)
    {
        var registrations = calls.Where(call => call.Name == "idf_component_register").ToArray();
        if (registrations.Length != 1)
        {
            throw Unsupported("需要唯一的顶层 idf_component_register");
        }
        var registration = registrations[0];
        var sections = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<Argument>();
        string? section = null;
        int? next = null;
        foreach (var argument in registration.Arguments)
        {
            if (IdfKeywords.Contains(argument.Value))
            {
                if (section == "SRCS")
                {
                    next = argument.Start;
                }
                section = argument.Value;
                if (!sections.Add(section))
                {
                    throw Unsupported("组件注册参数重复：" + section);
                }
                continue;
            }
            if (section is null || section == "WHOLE_ARCHIVE")
            {
                throw Unsupported("组件参数归属不明确");
            }
            if (section == "SRCS")
            {
                sources.Add(argument);
            }
        }
        if (sections.Contains("SRC_DIRS") || sections.Contains("EXCLUDE_SRCS"))
        {
            throw Unsupported("组件使用 SRC_DIRS/EXCLUDE_SRCS 自动收集或排除源码，请在当前目录规则中核对，不改写为逐文件列表");
        }
        // 无 SRCS 时可补充一个明确列表；空 SRCS 已存在时只添加路径，不能生成重复关键字。
        targets.Add(new("当前组件", sources, registration, next ?? registration.Close));
        idfHasSourcesKeyword = sections.Contains("SRCS");
    }
    private bool idfHasSourcesKeyword;
    private static string Name(Argument argument) => !argument.Escaped && Regex.IsMatch(argument.Value, "^[A-Za-z0-9_.+-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
        ? argument.Value : throw Unsupported("目标名称使用了变量或不支持的表达式");
    private static string Quote(string value) => "\"" + value + "\"";
    internal static StudioXException Unsupported(string reason) => new("SOURCE_CMAKE_UNSUPPORTED", "无法自动登记源码：" + reason + "。可打开构建文件手工调整；未修改工程。");
}
