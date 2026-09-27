namespace StudioX.Engine.Lvgl;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>扫描是只读的；所有候选明确返回，绝不按文件夹名或更新时间擅自选库。</summary>
public sealed class LvglProjectInspector
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { ".build", ".git", ".studiox", "bin", "obj", "node_modules", "__pycache__" };
    // 工程外扫描继承 MCP 只读外部目录的子目录边界；不能扩大已有授权范围。
    private static readonly HashSet<string> ExternalExcluded = new(StringComparer.OrdinalIgnoreCase)
        { "build", "debug", "release", "artifacts", "secrets", "credentials", "private", "keys", "certs" };
    internal static readonly string[] Required83Files = ["src/core/lv_obj.c", "src/hal/lv_hal_disp.c", "src/misc/lv_mem.c", "src/lv_conf_internal.h", "src/extra/lv_extra.c"];
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase) { ".c", ".cpp", ".cc", ".cxx" };
    private static readonly HashSet<string> Resources = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".bin", ".ttf", ".otf", ".svg" };

    public Task<LvglDiscoveryReport> DiscoverAsync(string project, IReadOnlyList<string>? searchDirectories = null, CancellationToken token = default) =>
        Task.Run(() => Discover(Path.GetFullPath(project), searchDirectories, token), token);
    public Task<LvglUiInspection> InspectUiAsync(string project, string libraryDirectory, string uiDirectory, CancellationToken token = default) =>
        Task.Run(() => Inspect(Path.GetFullPath(project), libraryDirectory, uiDirectory, token), token);
    public Task<LvglSetupValidation> ValidateSetupAsync(string project, LvglPreviewConfiguration configuration, CancellationToken token = default) =>
        Task.Run(() => ValidateSetup(Path.GetFullPath(project), configuration, token), token);

    private static LvglDiscoveryReport Discover(string project, IReadOnlyList<string>? searchDirectories, CancellationToken token)
    {
        var diagnostics = new List<LvglDiagnostic>();
        var roots = new List<string>(searchDirectories ?? ["."]);
        if (searchDirectories is null)
        {
            var existing = Path.Combine(project, ".studiox", "lvgl-preview.json");
            if (File.Exists(existing))
            {
                try
                {
                    var configuration = JsonSerializer.Deserialize<LvglPreviewConfiguration>(File.ReadAllText(existing), JsonStore.Options);
                    if (configuration is not null)
                    {
                        roots.Add(configuration.LvglDirectory);
                    }
                }
                catch (Exception ex) when (IsReadError(ex)) { diagnostics.Add(new("LVGL_EXISTING_CONFIGURATION", "warning", ex.Message, Relative(project, existing))); }
            }
        }
        var candidates = new Dictionary<string, LvglLibraryCandidate>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in roots)
        {
            token.ThrowIfCancellationRequested();
            string root;
            try
            {
                // 绝对路径仅用于明确选择/授权的扫描范围；保存的工程配置仍要求相对路径。
                root = searchDirectories is not null && Path.IsPathFullyQualified(relative)
                    ? ResolveAbsoluteSearchPath(relative) : ResolveRelative(project, relative);
            }
            catch (Exception ex) when (IsReadError(ex)) { diagnostics.Add(new("LVGL_SEARCH_PATH", "error", ex.Message, relative)); continue; }
            foreach (var directory in Walk(project, root, diagnostics, token, visited))
            {
                if (!IsLibraryRoot(directory))
                {
                    continue;
                }
                var candidate = ReadLibrary(project, directory, true, token);
                candidates[directory] = candidate;
            }
        }
        var ordered = candidates.Values.OrderBy(item => item.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var group in ordered.Where(item => item.SourceFingerprint is not null).GroupBy(item => item.SourceFingerprint))
        {
            if (group.Count() > 1)
            {
                diagnostics.Add(new("LVGL_DUPLICATE_LIBRARY", "info", "以下目录的核心源码内容相同；请选择其中一份：" + string.Join("、", group.Select(item => item.Directory))));
            }
        }
        foreach (var group in ordered.Where(item => item.Version is not null).GroupBy(item => item.Version))
        {
            if (group.Select(item => item.SourceFingerprint).Distinct().Count() > 1)
            {
                diagnostics.Add(new("LVGL_MODIFIED_LIBRARY", "warning", "同为 LVGL " + group.Key + " 的目录存在不同源码，不能只按版本号视为相同库。"));
            }
        }
        if (ordered.Length == 0)
        {
            diagnostics.Add(new("LVGL_NOT_FOUND", "warning", "未找到同时含 lvgl.h 和 src 的真实库根。可选择共享库所在目录继续扫描。"));
        }
        return new(project, ordered, diagnostics);
    }

    private static LvglUiInspection Inspect(string project, string libraryDirectory, string uiDirectory, CancellationToken token)
    {
        var library = ResolveRelative(project, libraryDirectory);
        var ui = ResolveRelative(project, uiDirectory);
        if (!Contained(project, ui))
        {
            throw new StudioXException("LVGL_UI_PATH", "UI 目录必须位于当前工程内；外部 UI 请先明确导入工程。");
        }
        var diagnostics = new List<LvglDiagnostic>();
        var sources = new List<string>();
        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = new List<string>();
        var entries = new List<LvglUiEntryPoint>();
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = Walk(project, ui, diagnostics, token).ToArray();
        foreach (var directory in directories)
        {
            if (IsLibraryRoot(directory))
            {
                libraries.Add(directory);
            }
        }
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            var embedded = libraries.FirstOrDefault(root => Contained(root, directory));
            if (embedded is not null)
            {
                if (embedded.Equals(directory, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new("UI_EMBEDDED_LIBRARY", "info", "库核心由选定 LVGL 单独编译，不将此目录当作 UI 源：" + Relative(project, embedded), Relative(project, embedded)));
                }
                continue;
            }
            foreach (var file in Files(project, directory, diagnostics, token))
            {
                var relative = Relative(project, file);
                var extension = Path.GetExtension(file);
                if (extension.Equals(".h", StringComparison.OrdinalIgnoreCase) || extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase))
                {
                    includes.Add(Relative(project, directory));
                }
                if (IsConfigurationHeader(file))
                {
                    headers.Add(relative);
                }
                if (Resources.Contains(extension))
                {
                    var resourceDirectory = Relative(project, directory);
                    if (resourceDirectory == ".")
                    {
                        diagnostics.Add(new("UI_RESOURCE_ROOT", "warning", "资源文件位于工程根。请放入独立资源子目录并显式映射，避免把整个工程复制到运行目录。", relative));
                    }
                    else
                    {
                        resources.Add(resourceDirectory);
                    }
                }
                if (!Sources.Contains(extension))
                {
                    continue;
                }
                string? source = ReadText(project, file, diagnostics);
                if (source is null)
                {
                    sources.Add(relative);
                    continue;
                }
                var clean = StripComments(source);
                if (Regex.IsMatch(clean, @"\b(?:int|void)\s+main\s*\(", RegexOptions.CultureInvariant))
                {
                    diagnostics.Add(new("UI_MAIN_SOURCE", "warning", "此文件定义 main；PC 宿主已有主循环，请拆出创建控件的入口。默认不加入。", relative));
                    continue;
                }
                if (HasHardwareDependency(clean))
                {
                    diagnostics.Add(new("UI_HARDWARE_SOURCE", "warning", "此文件引用 MCU/HAL/RTOS/汇编；默认不加入。需要先拆出纯 UI，或选择明确的 PC 实现。", relative));
                    continue;
                }
                sources.Add(relative);
                foreach (Match match in Regex.Matches(clean, "(?m)^[ \\t]*(?<linkage>extern\\s+\"C\"\\s+)?void\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*\\(\\s*(?:void)?\\s*\\)\\s*\\{", RegexOptions.CultureInvariant))
                {
                    var cpp = !extension.Equals(".c", StringComparison.OrdinalIgnoreCase);
                    var needsLinkage = cpp && !match.Groups["linkage"].Success;
                    entries.Add(new(match.Groups["name"].Value, relative, needsLinkage));
                    if (needsLinkage)
                    {
                        diagnostics.Add(new("UI_CPP_LINKAGE", "warning", "C++ 入口需要 extern \"C\" 导出，或显式编写 C 接口包装。", relative));
                    }
                }
                foreach (Match match in Regex.Matches(clean, "\"(?<file>[^\"\\r\\n]+\\.(?:png|jpg|jpeg|bmp|gif|bin|ttf|otf))\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var name = match.Groups["file"].Value;
                    if (name.Length > 2 && char.IsLetter(name[0]) && name[1] == ':')
                    {
                        name = name[2..].TrimStart('/', '\\');
                    }
                    if (Path.IsPathRooted(name))
                    {
                        diagnostics.Add(new("UI_RESOURCE_ABSOLUTE", "warning", "资源路径绑定本机绝对位置，请改为资源映射中的相对路径：" + match.Groups["file"].Value, relative));
                        continue;
                    }
                    if (!File.Exists(Path.Combine(project, name)) && !File.Exists(Path.Combine(directory, name)))
                    {
                        diagnostics.Add(new("UI_RESOURCE_MISSING", "warning", "源代码提及的资源文件未找到：" + match.Groups["file"].Value, relative));
                    }
                }
            }
        }
        // 配置通常在工程 include/ 中，与 UI 导出目录分开；仅收集候选，不替用户选择。
        foreach (var directory in Walk(project, project, diagnostics, token))
        {
            foreach (var file in Files(project, directory, diagnostics, token))
            {
                if (IsConfigurationHeader(file))
                {
                    headers.Add(Relative(project, file));
                }
            }
        }
        if (!IsLibraryRoot(library))
        {
            diagnostics.Add(new("LVGL_LIBRARY", "error", "请选择真实 LVGL 库根。", libraryDirectory));
        }
        if (sources.Count == 0)
        {
            diagnostics.Add(new("UI_NO_SOURCE", "error", "没有推荐的纯 UI C/C++ 源文件；硬件/main 文件仍在诊断中列出，可先拆分。", uiDirectory));
        }
        if (entries.Count == 0)
        {
            diagnostics.Add(new("UI_NO_ENTRY", "warning", "未找到非 static 的 void(void) 函数定义；可明确指定实际入口，或新增只创建控件的包装。", uiDirectory));
        }
        if (headers.Count == 0)
        {
            diagnostics.Add(new("UI_NO_CONFIGURATION", "warning", "UI 目录没有 lv_conf 头；请选择工程现有配置，或明确创建 PC 配置。", uiDirectory));
        }
        return new(project, Relative(project, library), Relative(project, ui), sources.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            includes.Order(StringComparer.OrdinalIgnoreCase).ToArray(), headers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            entries.OrderBy(entry => entry.Name.Contains("init", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray(),
            resources.Order(StringComparer.OrdinalIgnoreCase).ToArray(), diagnostics);
    }

    private static LvglSetupValidation ValidateSetup(string project, LvglPreviewConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var diagnostics = new List<LvglDiagnostic>();
        LvglLibraryCandidate? library = null;
        try
        {
            LvglPreviewBuilder.ValidateRequiredFields(configuration);
            var root = ResolveRelative(project, configuration.LvglDirectory);
            library = ReadLibrary(project, root, true, token);
            diagnostics.AddRange(library.Diagnostics);
            LvglPreviewBuilder.Validate(project, configuration);
            var entryFound = false;
            foreach (var relative in configuration.SourceFiles)
            {
                token.ThrowIfCancellationRequested();
                var file = ResolveRelative(project, relative);
                var source = ReadText(project, file, diagnostics);
                if (source is not null && HasHardwareDependency(StripComments(source)))
                {
                    diagnostics.Add(new("UI_HARDWARE_SOURCE", "warning", "选中的源包含硬件/RTOS依赖；需确认其已由 PC 实现替换。", relative));
                }
                if (source is not null && Regex.IsMatch(StripComments(source), "(?m)^[ \\t]*(?:extern\\s+\"C\"\\s+)?void\\s+" + Regex.Escape(configuration.EntryPoint) + @"\s*\(\s*(?:void)?\s*\)\s*\{", RegexOptions.CultureInvariant))
                {
                    entryFound = true;
                }
            }
            if (!entryFound)
            {
                diagnostics.Add(new("UI_ENTRY_MISSING", "error", "选中的源文件中未找到非 static 的 void " + configuration.EntryPoint + "(void) 定义；请选择实际入口或显式增加 PC 包装。"));
            }
            var header = File.ReadAllText(PathBoundary.Resolve(project, configuration.ConfigurationHeader));
            var depth = Macro(StripComments(header), "LV_COLOR_DEPTH");
            if (depth is not null && depth != configuration.ColorDepth)
            {
                diagnostics.Add(new("LVGL_COLOR_DEPTH", "error", "配置头的 LV_COLOR_DEPTH 与预览配置不一致。", configuration.ConfigurationHeader));
            }
            var clean = StripComments(header);
            if ((configuration.ResourceDirectories?.Count ?? 0) > 0 && !Regex.IsMatch(clean, @"(?m)^\s*#\s*define\s+LV_USE_FS_WIN32\s+\(?1\)?\b", RegexOptions.CultureInvariant))
            {
                diagnostics.Add(new("UI_RESOURCE_DRIVER", "warning", "已映射运行时资源；请确认配置启用相应文件系统驱动及图像/字体解码器。Win32 可启用 LV_USE_FS_WIN32 并设置驱动字母。", configuration.ConfigurationHeader));
            }
        }
        catch (Exception ex) when (IsReadError(ex)) { diagnostics.Add(new(ex is StudioXException studio ? studio.Code : "LVGL_INSPECTION_IO", "error", ex.Message)); }
        return new(!diagnostics.Any(item => item.Severity == "error"), library, diagnostics);
    }

    internal static LvglLibraryCandidate ReadLibrary(string project, string directory, bool fingerprint, CancellationToken token)
    {
        var diagnostics = new List<LvglDiagnostic>();
        var relative = Relative(project, directory);
        var header = Path.Combine(directory, "lvgl.h");
        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsLibraryRoot(directory))
            {
                return new(relative, null, false, false, "", null, ["lvgl.h", "src"], [new("LVGL_LIBRARY", "error", "目录不是 LVGL 根（需要 lvgl.h 和 src）。", relative)]);
            }
            ResolveAbsoluteSearchPath(directory);
            if (Path.IsPathRooted(relative))
            {
                diagnostics.Add(new("LVGL_LIBRARY_VOLUME", "warning", "库与工程不在同一磁盘，无法生成可移植相对配置。请先将库明确导入工程所在磁盘，再配置预览。", relative));
            }
            if ((File.GetAttributes(header) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(Path.Combine(directory, "src")) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("LVGL_PATH_LINK", "LVGL 头文件或源码目录是重解析点。");
            }
            var major = (int?)null;
            var minor = (int?)null;
            var patch = (int?)null;
            foreach (var versionPath in new[] { "lvgl.h", "lv_version.h", "src/lvgl.h", "src/lv_version.h" })
            {
                var file = Path.Combine(directory, versionPath);
                if (!File.Exists(file))
                {
                    continue;
                }
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("LVGL_PATH_LINK", "版本头文件不能是重解析点。");
                }
                var source = ReadText(project, file, diagnostics);
                if (source is null)
                {
                    continue;
                }
                source = StripComments(source);
                var nextMajor = Macro(source, "LVGL_VERSION_MAJOR");
                var nextMinor = Macro(source, "LVGL_VERSION_MINOR");
                var nextPatch = Macro(source, "LVGL_VERSION_PATCH");
                if (nextMajor is not null && major is not null && nextMajor != major || nextMinor is not null && minor is not null && nextMinor != minor || nextPatch is not null && patch is not null && nextPatch != patch)
                {
                    diagnostics.Add(new("LVGL_VERSION_CONFLICT", "error", "库内版本头的版本宏相互冲突。", Relative(project, file)));
                }
                major ??= nextMajor;
                minor ??= nextMinor;
                patch ??= nextPatch;
            }
            var version = major is not null && minor is not null && patch is not null ? $"{major}.{minor}.{patch}" : null;
            var supported = major == 8 && minor == 3 && patch is not null && !diagnostics.Any(item => item.Severity == "error");
            if (version is null)
            {
                diagnostics.Add(new("LVGL_VERSION_UNKNOWN", "error", "未找到完整的版本宏；不按文件夹名称猜测版本。", relative));
            }
            else if (!supported)
            {
                diagnostics.Add(new("LVGL_VERSION", "error", "当前预览支持 LVGL 8.3.x，所选目录为 " + version, relative));
            }
            var missing = (supported ? Required83Files : new[] { "src/core/lv_obj.c", "src/lv_conf_internal.h" }).Where(path => !File.Exists(Path.Combine(directory, path))).ToArray();
            if (missing.Length > 0)
            {
                diagnostics.Add(new("LVGL_LIBRARY_INCOMPLETE", "error", "缺少核心文件：" + string.Join("、", missing), relative));
            }
            var headerSha = Sha(header, token);
            string? coreSha = null;
            if (fingerprint)
            {
                var indexed = new List<string> { "lvgl.h=" + headerSha };
                var versionHeader = Path.Combine(directory, "lv_version.h");
                if (File.Exists(versionHeader))
                {
                    indexed.Add("lv_version.h=" + Sha(versionHeader, token));
                }
                foreach (var root in Walk(project, Path.Combine(directory, "src"), diagnostics, token))
                {
                    foreach (var file in Files(project, root, diagnostics, token).Where(file => Path.GetExtension(file).ToLowerInvariant() is ".c" or ".h"))
                    {
                        indexed.Add(Relative(directory, file) + "=" + Sha(file, token));
                    }
                }
                indexed.Sort(StringComparer.Ordinal);
                coreSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', indexed)))).ToLowerInvariant();
            }
            return new(relative, version, supported, missing.Length == 0, headerSha, coreSha, missing, diagnostics);
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            diagnostics.Add(new(ex is StudioXException studio ? studio.Code : "LVGL_LIBRARY_IO", "error", ex.Message, relative));
            return new(relative, null, false, false, "", null, [], diagnostics);
        }
    }

    internal static bool IsLibraryRoot(string directory) => File.Exists(Path.Combine(directory, "lvgl.h")) && Directory.Exists(Path.Combine(directory, "src"));
    internal static bool IsExcludedDirectory(string name) => Excluded.Contains(name);
    private static bool IsExcludedExternalName(string name) => name.StartsWith('.') || Excluded.Contains(name) || ExternalExcluded.Contains(name)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase) || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase)
        || name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) || name.StartsWith("token.", StringComparison.OrdinalIgnoreCase);
    private static bool IsConfigurationHeader(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("lv_conf", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(name).Equals(".h", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("lv_conf_internal.h", StringComparison.OrdinalIgnoreCase) && !name.Equals("lv_conf_template.h", StringComparison.OrdinalIgnoreCase);
    }
    internal static string? FindLibraryAncestor(string path)
    {
        for (var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (IsLibraryRoot(directory))
            {
                return directory;
            }
        }
        return null;
    }
    internal static string ResolveRelative(string project, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Any(c => c < 32 || ":\"|?*;$[]".Contains(c)))
        {
            throw new StudioXException("LVGL_PATH", "路径必须是正斜杠分隔的相对路径：" + relative);
        }
        var path = Path.GetFullPath(Path.Combine(project, relative));
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("LVGL_PATH_LINK", "路径包含重解析点：" + relative);
            }
        }
        return path;
    }
    private static string ResolveAbsoluteSearchPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new StudioXException("LVGL_SEARCH_PATH", "扫描目录必须是普通绝对路径。");
        }
        var full = Path.GetFullPath(path);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("LVGL_PATH_LINK", "扫描路径包含重解析点：" + path);
            }
        }
        return full;
    }
    internal static bool Contained(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    internal static string StripComments(string source) => Regex.Replace(source, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|/\\*[\\s\\S]*?\\*/|//[^\\r\\n]*",
        match => match.Value.StartsWith("/", StringComparison.Ordinal) ? " " : match.Value, RegexOptions.CultureInvariant);
    internal static bool HasHardwareDependency(string source) => Regex.IsMatch(source, @"\b(?:HAL_[A-Za-z0-9_]+|GPIO_[A-Za-z0-9_]+|RCC_[A-Za-z0-9_]+|ADC_[A-Za-z0-9_]+|USART_[A-Za-z0-9_]+|vTask[A-Za-z0-9_]+|xTask[A-Za-z0-9_]+|__asm__?|SystemInit)\b|FreeRTOS\.h|ch32v[0-9]+\.h|stm32[a-z0-9_]*\.h", RegexOptions.CultureInvariant);
    internal static int? Macro(string source, string name)
    {
        var match = Regex.Match(source, @"(?m)^\s*#\s*define\s+" + Regex.Escape(name) + @"\s+\(?\s*(?<value>[0-9]+)\s*\)?(?:\s|$)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value) ? value : null;
    }
    private static IEnumerable<string> Walk(string project, string root, List<LvglDiagnostic> diagnostics, CancellationToken token, HashSet<string>? visited = null)
    {
        visited ??= new(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (!visited.Add(directory))
            {
                continue;
            }
            if (!Contained(project, directory) && IsExcludedExternalName(Path.GetFileName(directory)))
            {
                diagnostics.Add(new("LVGL_EXTERNAL_PATH_SKIPPED", "info", "外部扫描跳过受保护目录。", Relative(project, directory)));
                continue;
            }
            if (!Directory.Exists(directory))
            {
                diagnostics.Add(new("LVGL_DIRECTORY_MISSING", "error", "目录不存在。", Relative(project, directory)));
                continue;
            }
            string[] children;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(new("LVGL_LINK_SKIPPED", "warning", "跳过重解析目录。", Relative(project, directory)));
                    continue;
                }
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (IsReadError(ex)) { diagnostics.Add(new("LVGL_DIRECTORY_IO", "error", ex.Message, Relative(project, directory))); continue; }
            yield return directory;
            foreach (var child in children.OrderDescending(StringComparer.OrdinalIgnoreCase))
            {
                if (!Excluded.Contains(Path.GetFileName(child)) && (Contained(project, child) || !IsExcludedExternalName(Path.GetFileName(child))))
                {
                    pending.Push(child);
                }
            }
        }
    }
    private static IEnumerable<string> Files(string project, string directory, List<LvglDiagnostic> diagnostics, CancellationToken token)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (IsReadError(ex)) { diagnostics.Add(new("LVGL_DIRECTORY_IO", "error", ex.Message, Relative(project, directory))); yield break; }
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!Contained(project, file) && IsExcludedExternalName(Path.GetFileName(file)))
            {
                continue;
            }
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add(new("LVGL_LINK_SKIPPED", "warning", "跳过重解析文件。", Relative(project, file)));
                    continue;
                }
            }
            catch (Exception ex) when (IsReadError(ex)) { diagnostics.Add(new("LVGL_SOURCE_IO", "error", ex.Message, Relative(project, file))); continue; }
            yield return file;
        }
    }
    private static string? ReadText(string project, string file, List<LvglDiagnostic> diagnostics)
    {
        try
        {
            if (new FileInfo(file).Length > 4 * 1024 * 1024)
            {
                diagnostics.Add(new("UI_SOURCE_LARGE", "info", "源文件较大，仍列为候选，但未进行文本依赖和入口检查。", Relative(project, file)));
                return null;
            }
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (IsReadError(ex)) { diagnostics.Add(new("LVGL_SOURCE_IO", "error", ex.Message, Relative(project, file))); return null; }
    }
    private static string Sha(string path, CancellationToken token)
    {
        using var source = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = source.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static bool IsReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or StudioXException or JsonException or ArgumentException;
}
