namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>只读核对生成配置的归属，不运行用户 CMake、不猜测 SDK、不改写原生数据库。</summary>
public static class AnalysisEnvironmentInspector
{
    public static async Task<AnalysisEnvironmentReport> InspectAsync(string directory, ProjectManifest project,
        ResolvedToolset? tools = null, CancellationToken token = default)
    {
        var root = Path.GetFullPath(directory);
        var database = PathBoundary.Resolve(root, ".build/compile_commands.json");
        if (!File.Exists(database))
        {
            return new(false, 0, []);
        }
        var issues = new List<AnalysisEnvironmentIssue>();
        var count = 0;
        var responses = new CompilationResponseFiles();
        var inspectedSpecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cache = PathBoundary.Resolve(root, ".build/CMakeCache.txt");
            if (File.Exists(cache))
            {
                if (new FileInfo(cache).Length > 8 * 1024 * 1024)
                {
                    throw new IOException("CMakeCache.txt 超出检查范围。");
                }
                foreach (var line in await File.ReadAllLinesAsync(cache, token).ConfigureAwait(false))
                {
                    if (line.StartsWith("CMAKE_HOME_DIRECTORY:", StringComparison.Ordinal))
                    {
                        var pair = line.Split('=', 2);
                        if (pair.Length != 2)
                        {
                            throw new JsonException("CMAKE_HOME_DIRECTORY 缺少路径值。");
                        }
                        CheckPath(pair[1], root, "LANGUAGE_CACHE_PROJECT", "分析缓存属于其他工程目录", true);
                    }
                }
            }
            if (project.Espressif is { } sdk)
            {
                var sdkconfigSource = PathBoundary.Resolve(root, "sdkconfig");
                var identityPath = PathBoundary.Resolve(root, ".build/studiox-idf-runtime.json");
                if (File.Exists(identityPath))
                {
                    using var identity = await ReadJsonAsync(identityPath, 1024 * 1024, token).ConfigureAwait(false);
                    CheckPropertyPath(identity.RootElement, "projectDirectory", root, "LANGUAGE_CACHE_PROJECT", "分析缓存属于其他工程目录");
                    if (tools is not null)
                    {
                        CheckPropertyPath(identity.RootElement, "toolsetDirectory", tools.RootDirectory, "LANGUAGE_CACHE_TOOLSET", "SDK 缓存仍引用迁移前的工具目录");
                    }
                    if (identity.RootElement.TryGetProperty("sdk", out var saved) && JsonSerializer.Deserialize<EspressifProjectSettings>(saved, JsonStore.Options) != sdk)
                    {
                        Add("LANGUAGE_CACHE_SDK", "分析缓存与锁定 SDK 不同", "请重新配置工程，使用当前锁定的 SDK 版本和目标。", saved.GetRawText(), true);
                    }
                    if (identity.RootElement.TryGetProperty("buildSettings", out var buildSettings) &&
                        JsonSerializer.Deserialize<ProjectBuildSettings>(buildSettings, JsonStore.Options) != await ProjectBuildSettings.ReadAsync(root, token).ConfigureAwait(false))
                    {
                        Add("LANGUAGE_CONFIG_CHANGED", "编译设置已变化", "当前编译设置尚未进入生成配置，请重新配置工程。", buildSettings.GetRawText());
                    }
                    var modulePath = PathBoundary.Resolve(root, EspressifModuleSettings.RelativePath);
                    var module = File.Exists(modulePath) ? await JsonStore.ReadAsync<EspressifModuleSettings>(modulePath, token).ConfigureAwait(false) : new();
                    if (identity.RootElement.TryGetProperty("moduleSettings", out var moduleSettings) &&
                        JsonSerializer.Deserialize<EspressifModuleSettings>(moduleSettings, JsonStore.Options) != module)
                    {
                        Add("LANGUAGE_CONFIG_CHANGED", "ESP 模组设置已变化", "模组设置尚未进入生成配置，请核对设置并重新配置工程。", moduleSettings.GetRawText());
                    }
                }
                var descriptionPath = PathBoundary.Resolve(root, ".build/project_description.json");
                if (File.Exists(descriptionPath))
                {
                    using var description = await ReadJsonAsync(descriptionPath, 8 * 1024 * 1024, token).ConfigureAwait(false);
                    CheckPropertyPath(description.RootElement, "project_path", root, "LANGUAGE_CACHE_PROJECT", "SDK 描述属于其他工程目录");
                    if (tools is not null)
                    {
                        CheckPropertyPath(description.RootElement, "idf_path", tools.ResourceDirectory("idf"), "LANGUAGE_CACHE_SDK", "分析配置引用了另一套 SDK");
                    }
                    if (description.RootElement.TryGetProperty("target", out var target) && target.GetString() != sdk.Target)
                    {
                        Add("LANGUAGE_CACHE_TARGET", "生成配置的芯片目标不同", "请重新配置当前工程，不能沿用另一目标的外设与宏定义。", "target=" + target.GetString(), true);
                    }
                    if (description.RootElement.TryGetProperty("config_file", out var configFile))
                    {
                        var selected = Path.GetFullPath(configFile.GetString()!, root);
                        if (Inside(root, selected))
                        {
                            sdkconfigSource = selected;
                        }
                    }
                }
                var configPath = PathBoundary.Resolve(root, ".build/config/sdkconfig.json");
                if (File.Exists(configPath))
                {
                    using var config = await ReadJsonAsync(configPath, 8 * 1024 * 1024, token).ConfigureAwait(false);
                    if (config.RootElement.TryGetProperty("IDF_TARGET", out var target) && target.GetString() != sdk.Target)
                    {
                        Add("LANGUAGE_CACHE_TARGET", "sdkconfig 生成目标不同", "请核对 SDK 配置并重新配置工程。", "IDF_TARGET=" + target.GetString(), true);
                    }
                    if (File.Exists(sdkconfigSource))
                    {
                        if (new FileInfo(sdkconfigSource).Length > 8 * 1024 * 1024)
                        {
                            throw new IOException("sdkconfig 超出检查范围。");
                        }
                        foreach (var sourceLine in await File.ReadAllLinesAsync(sdkconfigSource, token).ConfigureAwait(false))
                        {
                            var line = sourceLine.Trim();
                            var pair = line.StartsWith("CONFIG_", StringComparison.Ordinal) ? line[7..].Split('=', 2)
                                : line.StartsWith("# CONFIG_", StringComparison.Ordinal) && line.EndsWith(" is not set", StringComparison.Ordinal)
                                    ? [line[9..^11], "n"] : [];
                            // 废弃名称由 SDK 自己转换，只比较生成 JSON 中存在的同名键，避免猜测版本迁移规则。
                            if (pair.Length != 2 || !config.RootElement.TryGetProperty(pair[0], out var saved))
                            {
                                continue;
                            }
                            if (!SameConfigValue(pair[1], saved))
                            {
                                Add("LANGUAGE_SDKCONFIG_CHANGED", "原生 SDK 配置已变化", "sdkconfig 与生成宏不同，请重新配置工程后再使用实时诊断。",
                                    pair[0] + ": sdkconfig=" + pair[1] + "; generated=" + saved.GetRawText());
                                break;
                            }
                        }
                    }
                    if (tools is not null && config.RootElement.TryGetProperty("LIBC_PICOLIBC", out var library) && library.ValueKind == JsonValueKind.True)
                    {
                        var compilerRoot = Path.GetDirectoryName(Path.GetDirectoryName(tools.Tool("gcc")))!;
                        var headers = Path.Combine(compilerRoot, "picolibc", "include");
                        if (!Directory.Exists(headers))
                        {
                            issues.Add(new("LANGUAGE_ESPRESSIF_LIBC", "所选标准库头文件缺失", "工程选择了 Picolibc，请恢复锁定版本的完整开发环境组件。", headers, NeedsToolRepair: true));
                        }
                    }
                }
            }
            using var json = await ReadJsonAsync(database, 64 * 1024 * 1024, token).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("compile_commands.json 必须为数组。");
            }
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var working = Path.GetFullPath(entry.GetProperty("directory").GetString()!, root);
                var file = Path.GetFullPath(entry.GetProperty("file").GetString()!, working);
                if (Path.GetExtension(file).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx"))
                {
                    continue;
                }
                count++;
                if (!Inside(root, working))
                {
                    Add("LANGUAGE_DATABASE_PROJECT", "编译数据库仍引用其他工程目录", "工程已移动或复制，请预览并重建配置缓存。", "directory=" + working, true);
                }
                var arguments = entry.TryGetProperty("arguments", out var values)
                    ? values.EnumerateArray().Select(value => value.GetString()!).ToArray()
                    : CodeIntelligenceService.SplitCMakeCommand(entry.GetProperty("command").GetString()!);
                if (arguments.Length == 0)
                {
                    throw new JsonException("编译命令为空：" + file);
                }
                InspectSpecs(responses.Expand(arguments, working));
                if (tools is not null && project.Espressif is not null)
                {
                    var compiler = Path.GetFullPath(arguments[0], working);
                    if (!MatchesCompiler(compiler, tools, project.Espressif))
                    {
                        Add("LANGUAGE_DATABASE_TOOLSET", "编译数据库与锁定编译器不同", "请重新配置工程，使用当前锁定的开发环境组件。", "compiler=" + compiler, true);
                    }
                }
            }
            if (project.Espressif is not null)
            {
                foreach (var relative in new[] { ".build/toolchain/cflags", ".build/toolchain/cxxflags", ".build/bootloader/toolchain/cflags", ".build/bootloader/toolchain/cxxflags" })
                {
                    var path = PathBoundary.Resolve(root, relative);
                    if (File.Exists(path))
                    {
                        InspectSpecs(responses.Expand(["gcc", "@" + path], root));
                    }
                }
            }
            if (count == 0)
            {
                Add("LANGUAGE_DATABASE_EMPTY", "编译数据库没有 C/C++ 源文件", "请核对工程 CMake 的源文件声明并重新配置。", database);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or StudioXException)
        {
            Add(error is StudioXException studio && studio.Code == "LANGUAGE_ESPRESSIF_RESPONSE" ? studio.Code : "LANGUAGE_DATABASE_INVALID",
                "原生分析配置无法读取", "请查看原始诊断，配置生成期间可稍后重试；持续失败时重新配置工程。", error.ToString());
        }
        return new(true, count, issues)
        {
            ResponseFiles = responses.Paths
        };

        void Add(string code, string title, string detail, string raw, bool repair = false)
        {
            // 多个编译单元可能指向同一旧路径，报告按原因去重，避免大 SDK 淹没处理入口。
            if (!issues.Any(issue => issue.Code == code))
            {
                issues.Add(new(code, title, detail, raw, repair));
            }
        }
        void InspectSpecs(IEnumerable<string> arguments)
        {
            foreach (var argument in arguments)
            {
                var path = argument.StartsWith("--specs=", StringComparison.Ordinal) ? argument[8..]
                    : argument.StartsWith("-specs=", StringComparison.Ordinal) ? argument[7..] : "";
                // 相对 specs 由 GCC 自己查找；只判断原生生成参数中明确的绝对路径，不猜测搜索目录。
                if (Path.IsPathRooted(path) && inspectedSpecs.Add(path) && !File.Exists(path))
                {
                    Add("LANGUAGE_CACHE_SPECS", "原生参数引用的 specs 文件不存在", "请预览并重建参数缓存，再重新配置工程；不要修改 SDK 或手工拼接旧路径。", argument, true);
                }
            }
        }
        void CheckPath(string saved, string expected, string code, string title, bool repair)
        {
            if (!SamePath(saved, expected))
            {
                Add(code, title, "请预览并重建配置缓存，再重新配置工程；源码、sdkconfig 和工具锁保持保留。",
                "生成配置=" + saved + "\n当前锁定=" + expected, repair);
            }
        }
        void CheckPropertyPath(JsonElement value, string name, string expected, string code, string title)
        {
            if (value.TryGetProperty(name, out var saved))
            {
                CheckPath(saved.GetString()!, expected, code, title, true);
            }
        }
    }

    private static bool SamePath(string first, string second) => Path.IsPathRooted(first) && EspressifPathIdentity.NormalizePath(Path.GetFullPath(first)).Equals(
        EspressifPathIdentity.NormalizePath(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    private static bool SameConfigValue(string source, JsonElement generated)
    {
        if (generated.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return source == (generated.GetBoolean() ? "y" : "n");
        }
        if (generated.ValueKind == JsonValueKind.String)
        {
            return source.StartsWith('"') && JsonSerializer.Deserialize<string>(source) == generated.GetString();
        }
        if (generated.ValueKind == JsonValueKind.Number && source.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(source.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && generated.TryGetUInt64(out var saved) && number == saved;
        }
        return source == generated.GetRawText();
    }
    internal static bool MatchesCompiler(string compiler, ResolvedToolset tools, EspressifProjectSettings sdk)
    {
        if (SamePath(compiler, tools.Tool("gcc")) || SamePath(compiler, tools.Tool("gxx")))
        {
            return true;
        }
        // Windows 构建引擎的 EspressifXtensaBinding 使用同目录的通用驱动及精确目标 SO，避开厂商启动器的短文件名冲突。
        // 这里仅核对分析路径；实际内容及目标动态配置仍由构建引擎完整校验。
        if (!OperatingSystem.IsWindows() || sdk.Framework != "esp-idf" || sdk.Target is not ("esp32" or "esp32s3"))
        {
            return false;
        }
        var bin = Path.GetDirectoryName(tools.Tool("gcc"))!;
        return SamePath(compiler, Path.Combine(bin, "xtensa-esp-elf-gcc.exe")) || SamePath(compiler, Path.Combine(bin, "xtensa-esp-elf-g++.exe"));
    }
    private static bool Inside(string root, string path)
    {
        root = EspressifPathIdentity.NormalizePath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = EspressifPathIdentity.NormalizePath(path);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<JsonDocument> ReadJsonAsync(string path, long limit, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length > limit)
        {
            throw new StudioXException("LANGUAGE_CONFIG_SIZE", "分析配置过大，未加载：" + path);
        }
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }
}
