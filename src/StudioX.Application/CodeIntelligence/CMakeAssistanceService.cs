namespace StudioX.Application.CodeIntelligence;

using StudioX.Foundation;
using StudioX.Engine;

public sealed record CMakeAssistance(IReadOnlyList<CodeSuggestion> Suggestions, CodeSignature? Signature);

/// <summary>独立于构建工具的离线编辑辅助；只读取当前文档、根配置和工程内的当前路径层级。</summary>
public sealed class CMakeAssistanceService
{
    public static bool Supports(string path) => Path.GetFileName(path).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".cmake", StringComparison.OrdinalIgnoreCase);

    public Task<CMakeAssistance> GetAsync(string? project, string path, string text, int offset, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            var context = CMakeSyntax.Read(text, offset, token);
            if (context.Suppressed) return new CMakeAssistance([], null);
            var signature = Signature(context);
            var declarations = CMakeSyntax.Read(text, text.Length, token).Calls.ToList();
            if (project is not null && !path.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(project, "CMakeLists.txt")))
            {
                var root = await new ProjectFileService().ReadAsync(project, "CMakeLists.txt", token);
                declarations.AddRange(CMakeSyntax.Read(root.Text, root.Text.Length, token).Calls);
            }
            // 只索引生成器拥有的固定入口，不执行或递归猜测用户的 include/add_subdirectory。
            if (project is not null)
                foreach (var managedPath in new[] { CMakeGenerator.DeviceListPath, CMakeGenerator.PlatformPath })
                {
                    if (path.Equals(managedPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(project, managedPath))) continue;
                    var managed = await new ProjectFileService().ReadAsync(project, managedPath, token);
                    if (CMakeGenerator.IsManagedFile(managedPath, managed.Text)) declarations.AddRange(CMakeSyntax.Read(managed.Text, managed.Text.Length, token).Calls);
                }
            var variables = new Dictionary<string, string>(CMakeCatalog.Variables, StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            // CubeMX 通常先 set(CMAKE_PROJECT_NAME ...)，再以变量创建目标。
            string ProjectTargetName(string name)
            {
                if (name == "${PROJECT_NAME}" && declarations.FirstOrDefault(c => c.Name == "project") is { Arguments.Count: > 0 } definition)
                    name = definition.Arguments[0].Value;
                if (name == "${CMAKE_PROJECT_NAME}" && declarations.LastOrDefault(c => c.Name == "set" && c.Arguments.Count > 1 && c.Arguments[0].Value == "CMAKE_PROJECT_NAME") is { } assignment)
                    name = assignment.Arguments[1].Value;
                return name;
            }
            var functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in declarations)
            {
                if (declaration.Arguments.Count == 0) continue;
                var name = declaration.Arguments[0].Value;
                if (declaration.Name is "set" or "option" or "foreach" && Identifier(name)) variables[name] = "工程变量 · " + declaration.Name + "() 声明";
                if (declaration.Name is "add_executable" or "add_library" or "add_custom_target")
                {
                    name = ProjectTargetName(name);
                    if (!name.Contains('$')) targets.Add(name);
                }
                if (declaration.Name is "function" or "macro" && Identifier(name))
                {
                    functions[name] = $"{name}({string.Join(' ', declaration.Arguments.Skip(1).Select(a => a.Value))})";
                    foreach (var argument in declaration.Arguments.Skip(1).Where(a => Identifier(a.Value))) variables.TryAdd(argument.Value, "函数 / 宏形式参数");
                }
            }
            if (signature is null && context.Call is { } currentCall && declarations.FirstOrDefault(c => c.Name is "function" or "macro" && c.Arguments.Count > 0 && c.Arguments[0].Value.Equals(currentCall.Name, StringComparison.OrdinalIgnoreCase)) is { } function)
            {
                var parameters = function.Arguments.Skip(1).Select(a => a.Value).ToArray();
                signature = new(functions[function.Arguments[0].Value], "当前工程定义的函数 / 宏。", parameters, Math.Clamp(context.ArgumentIndex, 0, Math.Max(0, parameters.Length - 1)));
            }

            var suggestions = new List<CodeSuggestion>();
            var positions = new Dictionary<int, CodePosition>();
            var current = context.Current;
            var start = current is null ? offset : current.Start + (current.Quoted ? 1 : 0);
            var end = offset;
            var prefix = text[start..offset];
            while (end < text.Length && IsWord(text[end])) end++;
            var reference = prefix.LastIndexOf("${", StringComparison.Ordinal);
            var escapedReference = false;
            if (reference >= 0)
            {
                var escapes = 0;
                for (var i = reference - 1; i >= 0 && prefix[i] == '\\'; i--) escapes++;
                escapedReference = escapes % 2 == 1;
            }
            if (reference >= 0 && !escapedReference && prefix[(reference + 2)..].All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                start += reference + 2; prefix = text[start..offset];
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                if (end < text.Length && text[end] == '}') end++;
                foreach (var (name, description) in variables) Add(name, name + "}", "变量", description, 6);
                return Result();
            }
            // 不能把环境变量、生成器表达式或任意动态变量路径当作普通变量求值。
            if (prefix.Contains('$') && !prefix.Contains('/')) return Result();
            if (context.Call is null)
            {
                foreach (var command in CMakeCatalog.Commands.Values) Add(command.Name, command.Name, command.Description.Split('。')[0], command.Description + "\n" + command.Signature, 3);
                foreach (var (name, description) in functions) Add(name, name, "工程函数 / 宏", description, 3);
                return Result();
            }
            var call = context.Call;
            var index = context.ArgumentIndex;
            var args = call.Arguments.Select(a => a.Value).ToArray();
            string First() => args.FirstOrDefault() ?? "";
            void Keywords(IEnumerable<string> values, string detail = "参数")
            {
                foreach (var value in values) Add(value, value, detail, value switch
                {
                    "PRIVATE" => "仅影响当前目标。单片机应用工程通常使用 PRIVATE。",
                    "PUBLIC" => "影响当前目标，并传递给链接此目标的其他目标。",
                    "INTERFACE" => "仅传递给链接此目标的其他目标，不用于当前目标。",
                    _ => signature?.Documentation ?? "CMake 参数"
                }, 14);
            }
            void Targets() { foreach (var name in targets) Add(name, name, "工程目标", "由 add_executable / add_library / add_custom_target 声明。", 7); }
            void Variables(bool bare)
            {
                foreach (var (name, description) in variables) Add(name, bare ? name : "${" + name + "}", "变量", description, 6);
            }
            var paths = false; var directoriesOnly = false;
            if (call.Name.StartsWith("target_", StringComparison.Ordinal))
            {
                if (index == 0) Targets();
                else
                {
                    Keywords(CMakeCatalog.Scopes, "可见性");
                    if (index == 1 && call.Name is "target_include_directories" or "target_compile_options" or "target_link_options" or "target_link_directories") Keywords(["BEFORE"]);
                    if (index == 1 && call.Name == "target_include_directories") Keywords(["SYSTEM", "AFTER"]);
                    if (index >= 2)
                    {
                        if (call.Name == "target_link_libraries") Targets();
                        if (call.Name == "target_compile_features") Keywords(["c_std_99", "c_std_11", "c_std_17", "cxx_std_11", "cxx_std_14", "cxx_std_17", "cxx_std_20", "cxx_std_23"]);
                        paths = call.Name is "target_sources" or "target_include_directories" or "target_link_directories" or "target_precompile_headers";
                        directoriesOnly = call.Name is "target_include_directories" or "target_link_directories";
                        Variables(false);
                    }
                }
            }
            else switch (call.Name)
            {
                case "set":
                    if (index == 0) Variables(true);
                    else { Keywords(Values(First())); if (declarations.Any(c => c.Name == "option" && c.Arguments.FirstOrDefault()?.Value == First())) Keywords(["ON", "OFF"]); Keywords(["CACHE", "PARENT_SCOPE"]); if (args.Contains("CACHE")) Keywords(["BOOL", "STRING", "PATH", "FILEPATH", "INTERNAL", "FORCE"]); Variables(false); }
                    break;
                case "option": if (index == 2) Keywords(["ON", "OFF"]); break;
                case "unset": if (index == 0) Variables(true); else Keywords(["CACHE", "PARENT_SCOPE"]); break;
                case "project": if (index > 0) Keywords(["VERSION", "DESCRIPTION", "HOMEPAGE_URL", "LANGUAGES", "C", "CXX", "ASM", "NONE"]); break;
                case "cmake_minimum_required": if (index == 0) Keywords(["VERSION"]); break;
                case "add_executable": case "add_library":
                    if (index > 0) { paths = true; Variables(false); Keywords(call.Name == "add_library" ? ["STATIC", "SHARED", "MODULE", "OBJECT", "INTERFACE", "IMPORTED", "ALIAS", "EXCLUDE_FROM_ALL"] : ["EXCLUDE_FROM_ALL", "IMPORTED", "ALIAS"]); }
                    break;
                case "set_target_properties":
                    var properties = Array.IndexOf(args, "PROPERTIES");
                    if (properties < 0 || index <= properties) { Targets(); if (index > 0) Keywords(["PROPERTIES"]); }
                    else if ((index - properties) % 2 == 1) Keywords(CMakeCatalog.Properties, "目标属性");
                    else Keywords(Values(args[index - 1]));
                    break;
                case "get_target_property": if (index == 1) Targets(); else if (index == 2) Keywords(CMakeCatalog.Properties, "目标属性"); break;
                case "set_property": Keywords(["GLOBAL", "DIRECTORY", "TARGET", "SOURCE", "TEST", "CACHE", "APPEND", "APPEND_STRING", "PROPERTY"]); if (args.Contains("PROPERTY")) Keywords(CMakeCatalog.Properties); break;
                case "add_dependencies": Targets(); break;
                case "add_subdirectory": paths = true; directoriesOnly = true; if (index > 0) Keywords(["EXCLUDE_FROM_ALL"]); break;
                case "include": paths = index == 0; if (index > 0) Keywords(["OPTIONAL", "RESULT_VARIABLE", "NO_POLICY_SCOPE"]); break;
                case "configure_file": paths = index < 2; if (index >= 2) Keywords(["COPYONLY", "@ONLY", "ESCAPE_QUOTES", "NEWLINE_STYLE"]); break;
                case "add_custom_command":
                    if (index > 0 && args[index - 1] == "TARGET") Targets();
                    else Keywords(["TARGET", "OUTPUT", "PRE_BUILD", "PRE_LINK", "POST_BUILD", "COMMAND", "DEPENDS", "BYPRODUCTS", "WORKING_DIRECTORY", "COMMENT", "VERBATIM", "APPEND", "USES_TERMINAL", "COMMAND_EXPAND_LISTS"]);
                    if (index > 1) Variables(false); break;
                case "add_custom_target": if (index > 0) { Keywords(["ALL", "COMMAND", "DEPENDS", "BYPRODUCTS", "WORKING_DIRECTORY", "COMMENT", "VERBATIM", "SOURCES", "USES_TERMINAL"]); Variables(false); } break;
                case "if": case "elseif": case "while":
                    Keywords(["NOT", "AND", "OR", "DEFINED", "EXISTS", "TARGET", "COMMAND", "STREQUAL", "EQUAL", "LESS", "GREATER", "MATCHES", "IN_LIST", "ON", "OFF"]); Variables(true);
                    if (index > 0 && args[index - 1] == "TARGET") Targets(); break;
                case "list": if (index == 0) Keywords(["APPEND", "PREPEND", "INSERT", "REMOVE_ITEM", "REMOVE_AT", "REMOVE_DUPLICATES", "LENGTH", "GET", "FIND", "JOIN", "FILTER", "SORT", "REVERSE", "TRANSFORM", "SUBLIST"]); else Variables(index == 1); break;
                case "file": if (index == 0) Keywords(["GLOB", "GLOB_RECURSE", "READ", "WRITE", "APPEND", "COPY", "COPY_FILE", "MAKE_DIRECTORY", "REMOVE", "REMOVE_RECURSE", "RENAME", "GENERATE", "RELATIVE_PATH", "TO_CMAKE_PATH"]); else { Keywords(["CONFIGURE_DEPENDS", "RELATIVE", "DESTINATION"]); Variables(false); paths = true; } break;
                case "string": if (index == 0) Keywords(["REPLACE", "REGEX", "APPEND", "PREPEND", "CONCAT", "JOIN", "TOLOWER", "TOUPPER", "LENGTH", "SUBSTRING", "STRIP", "FIND", "COMPARE", "CONFIGURE"]); break;
                case "message": if (index == 0) Keywords(["STATUS", "WARNING", "AUTHOR_WARNING", "FATAL_ERROR", "SEND_ERROR", "NOTICE", "VERBOSE", "DEBUG", "TRACE"]); Variables(false); break;
                case "foreach": if (index > 0) { Keywords(["IN", "LISTS", "ITEMS", "RANGE", "ZIP_LISTS"]); Variables(true); } break;
                case "enable_language": Keywords(["C", "CXX", "ASM"]); break;
                case "include_guard": Keywords(["GLOBAL", "DIRECTORY"]); break;
                case "find_package": if (index > 0) Keywords(["REQUIRED", "QUIET", "COMPONENTS", "OPTIONAL_COMPONENTS", "CONFIG", "MODULE", "EXACT", "NO_DEFAULT_PATH"]); break;
                case "find_path": case "find_library": case "find_program": if (index > 0) { Keywords(["NAMES", "HINTS", "PATHS", "PATH_SUFFIXES", "NO_DEFAULT_PATH", "REQUIRED"]); Variables(false); } break;
                case "install": Keywords(["TARGETS", "FILES", "DIRECTORY", "DESTINATION", "RUNTIME", "LIBRARY", "ARCHIVE", "COMPONENT", "OPTIONAL"]); break;
                case "add_test": Keywords(["NAME", "COMMAND", "CONFIGURATIONS", "WORKING_DIRECTORY", "COMMAND_EXPAND_LISTS"]); break;
                default: if (!CMakeCatalog.Commands.TryGetValue(call.Name, out var command) || command.Parameters.Length > 0) Variables(false); break;
            }
            if (paths && project is not null)
            {
                var slash = prefix.LastIndexOf('/');
                var parent = slash < 0 ? "" : prefix[..(slash + 1)];
                if (slash >= 0) { suggestions.Clear(); start += slash + 1; prefix = text[start..offset]; }
                prefix = prefix.Replace("\\ ", " ", StringComparison.Ordinal).Replace("\\(", "(", StringComparison.Ordinal).Replace("\\)", ")", StringComparison.Ordinal).Replace("\\#", "#", StringComparison.Ordinal);
                // 按路径组成部分替换，保留用户已输入的目录、引号以及既有后缀。
                end = offset;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('/' or '\\' or '"' or ')' or ';')) end++;
                foreach (var entry in PathEntries(project, path, parent, token))
                {
                    if (directoriesOnly && !entry.IsDirectory) continue;
                    if (call.Name == "include" && !entry.IsDirectory && !entry.Name.EndsWith(".cmake", StringComparison.OrdinalIgnoreCase)) continue;
                    var insert = entry.Name.Replace("$", "\\$", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal);
                    if (current?.Quoted != true) insert = insert.Replace(" ", "\\ ", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal);
                    if (entry.IsDirectory && (end == text.Length || text[end] != '/')) insert += "/";
                    Add(entry.Name + (entry.IsDirectory ? "/" : ""), insert, entry.IsDirectory ? "工程目录" : "工程文件", entry.RelativePath, entry.IsDirectory ? 19 : 17);
                }
            }
            return Result();

            void Add(string label, string insert, string detail, string documentation, int kind)
            {
                if (!label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                suggestions.Add(new(label, insert, label, detail, documentation, kind,
                    new(Position(start), Position(end)), label));
            }
            CodePosition Position(int position)
            {
                if (!positions.TryGetValue(position, out var result)) positions[position] = result = CodePositions.FromOffset(text, position);
                return result;
            }
            CMakeAssistance Result() => new(suggestions.DistinctBy(s => s.FilterText).Take(150).ToArray(), signature);
        }, token);

    private static bool Identifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c == '_');
    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-';

    private static CodeSignature? Signature(CMakeContext context)
    {
        if (context.Call is not { } call || !CMakeCatalog.Commands.TryGetValue(call.Name, out var command)) return null;
        var parameters = command.Parameters;
        var active = Math.Min(context.ArgumentIndex, parameters.Length - 1);
        if (call.Name.StartsWith("target_", StringComparison.Ordinal) && parameters.Length == 3)
        {
            var scope = call.Arguments.FindLastIndex(a => CMakeCatalog.Scopes.Contains(a.Value));
            active = context.ArgumentIndex == 0 ? 0 : scope >= 0 && context.ArgumentIndex > scope ? 2 : 1;
        }
        return new(command.Signature, command.Description, parameters, Math.Max(0, active));
    }

    private static string[] Values(string variable) => variable switch
    {
        "CMAKE_BUILD_TYPE" => ["Debug", "Release", "RelWithDebInfo", "MinSizeRel"],
        "CMAKE_C_STANDARD" or "C_STANDARD" => ["90", "99", "11", "17", "23"],
        "CMAKE_CXX_STANDARD" or "CXX_STANDARD" => ["98", "11", "14", "17", "20", "23"],
        "CMAKE_SYSTEM_NAME" => ["Generic"], "CMAKE_TRY_COMPILE_TARGET_TYPE" => ["STATIC_LIBRARY", "EXECUTABLE"],
        "SUFFIX" => [".elf", ".a"], "LINKER_LANGUAGE" => ["C", "CXX", "ASM"],
        _ => variable.EndsWith("_REQUIRED", StringComparison.Ordinal) || variable.EndsWith("_EXTENSIONS", StringComparison.Ordinal) || variable is "CMAKE_EXPORT_COMPILE_COMMANDS" or "BUILD_SHARED_LIBS" or "POSITION_INDEPENDENT_CODE" ? ["ON", "OFF"] : []
    };

    private static IEnumerable<ProjectEntry> PathEntries(string project, string path, string parent, CancellationToken token)
    {
        var listDirectory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        var sourceDirectory = Path.GetFileName(path).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ? listDirectory : "";
        var relative = sourceDirectory.Length == 0 ? parent : sourceDirectory + "/" + parent;
        foreach (var (name, directory) in new[] { ("CMAKE_CURRENT_LIST_DIR", listDirectory), ("CMAKE_CURRENT_SOURCE_DIR", sourceDirectory), ("CMAKE_SOURCE_DIR", ""), ("PROJECT_SOURCE_DIR", "") })
            if (parent.StartsWith("${" + name + "}/", StringComparison.Ordinal)) relative = (directory.Length == 0 ? "" : directory + "/") + parent[(name.Length + 4)..];
        if (relative.Contains('$') || Path.IsPathRooted(relative) || relative.Contains('\\')) return [];
        var full = Path.GetFullPath(Path.Combine(project, relative));
        var normalized = Path.GetRelativePath(project, full).Replace('\\', '/').TrimEnd('/');
        if (normalized == ".") normalized = "";
        if (normalized.Length > 0)
        {
            // 上级目录可在工程内使用，但不越界、不跟随链接访问工程外的文件。
            if (normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal)) return [];
            full = PathBoundary.Resolve(project, normalized);
        }
        if (!Directory.Exists(full)) return [];
        token.ThrowIfCancellationRequested();
        var entries = new List<ProjectEntry>();
        foreach (var item in new DirectoryInfo(full).EnumerateFileSystemInfos().Take(5000))
        {
            token.ThrowIfCancellationRequested();
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.Name is ".git" or ".studiox" or "build") continue;
            entries.Add(new(item.Name, Path.GetRelativePath(project, item.FullName).Replace('\\', '/'), (item.Attributes & FileAttributes.Directory) != 0, false));
        }
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(300).ToArray();
    }
}
