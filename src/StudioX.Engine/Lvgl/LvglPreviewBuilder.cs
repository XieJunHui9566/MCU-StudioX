namespace StudioX.Engine.Lvgl;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>PC 使用独立 CMake 工程和原生编译器，不生成 MCU 烧录凭据。</summary>
public sealed class LvglPreviewBuilder(ToolsetCatalog toolsets)
{
    public const string ConfigurationPath = ".studiox/lvgl-preview.json";
    public const string BundledToolsetId = "pc.mingw";
    public const string BundledToolsetVersion = "1.0.0";
    public const string BundledCompilerId = "mingw-gcc-13.1.0";
    private readonly ProcessRunner runner = new();
    public string BundledCompilerPath => PathBoundary.Resolve(toolsets.RootDirectory, $"{BundledToolsetId}/{BundledToolsetVersion}/gcc/bin/gcc.exe");

    public async Task<LvglHostToolchain> ResolveBundledToolchainAsync(CancellationToken token = default, IProgress<string>? progress = null)
    {
        var resolved = await toolsets.ResolveAsync(BundledToolsetId, BundledToolsetVersion, BundledCompilerId, token, progress: progress);
        if (resolved.Manifest.Purpose != "windows-native")
        {
            throw new StudioXException("LVGL_TOOLSET_PURPOSE", "PC 编译器必须使用 windows-native 工具清单，不能替代为 MCU 交叉编译器。");
        }
        var inspected = await InspectToolchainAsync(resolved.Tool("gcc"), token);
        if (inspected.Version != "13.1.0" || resolved.Manifest.ComponentVersions?.GetValueOrDefault("gcc") != "13.1.0")
        {
            throw new StudioXException("LVGL_COMPILER_VERSION", "内置 PC GCC 的实际版本与发行清单不符。");
        }
        return inspected with
        {
            ToolsetId = BundledToolsetId,
            ToolsetVersion = BundledToolsetVersion,
            Fingerprint = resolved.Fingerprint,
            IsBundled = true
        };
    }

    public static async Task<LvglPreviewConfiguration> ReadConfigurationAsync(string project, CancellationToken token = default)
    {
        var root = Path.GetFullPath(project);
        var path = PathBoundary.Resolve(root, ConfigurationPath);
        if (!File.Exists(path))
        {
            throw new StudioXException("LVGL_CONFIGURATION_MISSING", "工程尚未配置 PC 预览，请添加 .studiox/lvgl-preview.json，指定共享 LVGL 和 UI 入口。");
        }
        if (new FileInfo(path).Length > 128 * 1024)
        {
            throw new StudioXException("LVGL_CONFIGURATION_SIZE", "LVGL 预览配置过大。");
        }
        var configuration = await JsonStore.ReadAsync<LvglPreviewConfiguration>(path, token);
        Validate(root, configuration);
        return configuration;
    }

    public static async Task SaveConfigurationAsync(string project, LvglPreviewConfiguration configuration, CancellationToken token = default)
    {
        var root = Path.GetFullPath(project);
        Validate(root, configuration);
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, ConfigurationPath), configuration, token);
    }

    public static async Task<LvglHostToolchain> InspectToolchainAsync(string gccPath, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new StudioXException("LVGL_HOST", "首版 LVGL PC 预览支持 Windows。");
        }
        if (!Path.IsPathFullyQualified(gccPath) || !File.Exists(gccPath) || !Path.GetExtension(gccPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("LVGL_COMPILER", "请选择已安装的 Windows MinGW GCC 可执行文件。");
        }
        gccPath = Path.GetFullPath(gccPath);
        var runner = new ProcessRunner();
        var environment = CompilerEnvironment(gccPath);
        var target = await runner.RunAsync(new(gccPath, ["-dumpmachine"], Path.GetDirectoryName(gccPath)!, TimeSpan.FromSeconds(10), environment,
            RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
        var version = await runner.RunAsync(new(gccPath, ["-dumpfullversion"], Path.GetDirectoryName(gccPath)!, TimeSpan.FromSeconds(10), environment,
            RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
        if (target.ExitCode != 0 || version.ExitCode != 0 || target.TimedOut || version.TimedOut || !target.StandardOutput.Trim().Equals("x86_64-w64-mingw32", StringComparison.Ordinal))
        {
            throw new StudioXException("LVGL_COMPILER_TARGET", "PC 预览需要 x86_64-w64-mingw32 GCC；不能使用 MCU 交叉编译器。\n" + target.StandardOutput + target.StandardError + version.StandardError);
        }
        await using var stream = File.OpenRead(gccPath);
        return new(gccPath, target.StandardOutput.Trim(), version.StandardOutput.Trim(), Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
    }

    public async Task<LvglPreviewBuildResult> BuildAsync(string project, LvglHostToolchain compiler, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var root = Path.GetFullPath(project);
        var configuration = await ReadConfigurationAsync(root, token);
        var checkedCompiler = compiler.IsBundled
            ? await ResolveBundledToolchainAsync(token, progress)
            : await InspectToolchainAsync(compiler.GccPath, token);
        if (checkedCompiler != compiler)
        {
            throw new StudioXException("LVGL_COMPILER_CHANGED", "原生 GCC 已变化，请重新选择开发环境组件，核对版本和指纹。");
        }
        var manifest = await ProjectService.ReadAsync(root, token);
        var tools = await toolsets.ResolveAsync(manifest.ToolsetId, manifest.ToolsetVersion, manifest.CompilerId, token);
        var directory = PathBoundary.Resolve(root, ".build/pc-preview");
        Directory.CreateDirectory(directory);
        var generated = Path.Combine(directory, "generated");
        Directory.CreateDirectory(generated);
        // 导出的 UI 常使用 lvgl/lvgl.h；明确转发所选公共头，不改变用户库的目录名称或内容。
        var publicHeaders = Path.Combine(generated, "lvgl");
        Directory.CreateDirectory(publicHeaders);
        var publicHeader = Path.Combine(Relative(root, configuration.LvglDirectory), "lvgl.h").Replace('\\', '/');
        await WriteChangedAsync(Path.Combine(publicHeaders, "lvgl.h"),
            "#ifndef STUDIOX_SELECTED_LVGL_PUBLIC_H\n#define STUDIOX_SELECTED_LVGL_PUBLIC_H\n#include \"" + publicHeader + "\"\n#endif\n", token);
        var assembly = typeof(LvglPreviewBuilder).Assembly;
        await using (var source = assembly.GetManifestResourceStream("StudioX.Engine.Resources.Lvgl.preview_host.c")
            ?? throw new StudioXException("LVGL_HOST_SOURCE", "发行版本缺少 LVGL 原生预览宿主。"))
        {
            using var reader = new StreamReader(source);
            await WriteChangedAsync(Path.Combine(generated, "preview_host.c"), await reader.ReadToEndAsync(token), token);
        }
        await WriteChangedAsync(Path.Combine(generated, "lv_port_clock.h"), "#ifndef STUDIOX_PC_CLOCK_H\n#define STUDIOX_PC_CLOCK_H\n#include <stdint.h>\nuint32_t lv_port_millis(void);\n#endif\n", token);
        await WriteChangedAsync(Path.Combine(generated, "lv_port.h"), "#ifndef STUDIOX_PC_PORT_H\n#define STUDIOX_PC_PORT_H\nvoid lv_port_panic(void);\n#endif\n", token);
        await WriteChangedAsync(Path.Combine(generated, "CMakeLists.txt"), GenerateCMake(root, generated, configuration), token);
        // 更换编译器不能沿用旧的 CMake 编译器缓存；只移除本功能生成的元数据。
        var stampPath = Path.Combine(directory, "compiler.json");
        if (File.Exists(stampPath))
        {
            var previous = await JsonStore.ReadAsync<LvglHostToolchain>(stampPath, token);
            if (previous != compiler)
            {
                var cache = Path.Combine(directory, "CMakeCache.txt");
                if (File.Exists(cache))
                {
                    File.Delete(cache);
                }
                var metadata = Path.GetFullPath(Path.Combine(directory, "CMakeFiles"));
                if (!metadata.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("LVGL_BUILD_PATH", "构建目录校验失败。");
                }
                if (Directory.Exists(metadata))
                {
                    Directory.Delete(metadata, true);
                }
            }
        }
        await JsonStore.WriteAsync(stampPath, compiler, token);
        var environment = CompilerEnvironment(compiler.GccPath, tools);
        var log = new StringBuilder();
        var executable = Path.Combine(directory, "bin", "preview-next.exe");
        var arguments = new List<string> { "-S", generated, "-B", directory, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_C_COMPILER=" + compiler.GccPath.Replace('\\', '/'), "-DCMAKE_MAKE_PROGRAM=" + tools.Tool("ninja").Replace('\\', '/') };
        var gxx = compiler.IsBundled
            ? (await toolsets.ResolveAsync(BundledToolsetId, BundledToolsetVersion, BundledCompilerId, token)).Tool("gxx")
            : Path.Combine(Path.GetDirectoryName(compiler.GccPath)!, "g++.exe");
        if (!File.Exists(gxx))
        {
            throw new StudioXException("LVGL_COMPILER", "原生开发环境组件缺少 g++.exe。");
        }
        arguments.Add("-DCMAKE_CXX_COMPILER=" + gxx.Replace('\\', '/'));
        var configured = await RunAsync(tools.Tool("cmake"), arguments, root, environment, log, progress, token);
        var success = configured;
        if (configured)
        {
            success = await RunAsync(tools.Tool("cmake"), ["--build", directory, "--parallel", Math.Min(Environment.ProcessorCount, 8).ToString()], root, environment, log, progress, token);
        }
        success &= File.Exists(executable);
        var result = new LvglPreviewBuildResult(success, executable, directory, log.ToString(), configuration);
        // 即使随后资源映射失败，也保留本轮原始编译诊断。
        await File.WriteAllTextAsync(Path.Combine(directory, "build.log"), result.Log, token);
        if (success)
        {
            var runtimeResources = PathBoundary.Resolve(directory, "resources-next");
            if (Directory.Exists(runtimeResources))
            {
                Directory.Delete(runtimeResources, recursive: true);
            }
            Directory.CreateDirectory(runtimeResources);
            foreach (var relative in configuration.ResourceDirectories ?? [])
            {
                await CopyResourcesAsync(root, relative, runtimeResources, token);
            }
            result = result with
            {
                RuntimeResourcesDirectory = runtimeResources
            };
        }
        return result;
    }

    private async Task<bool> RunAsync(string executable, IReadOnlyList<string> arguments, string root, Dictionary<string, string> environment,
        StringBuilder log, IProgress<string>? progress, CancellationToken token)
    {
        log.AppendLine(Path.GetFileName(executable) + " " + string.Join(" ", arguments));
        var result = await runner.RunAsync(new(executable, arguments, root, TimeSpan.FromMinutes(5), environment,
            RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: progress), token);
        log.Append(result.StandardOutput).Append(result.StandardError);
        if (result.OutputTruncated)
        {
            log.AppendLine("[构建输出超过保留容量，已截断]");
        }
        if (result.TimedOut)
        {
            log.AppendLine("[构建超时，工具进程树已停止]");
        }
        return result.ExitCode == 0 && !result.TimedOut;
    }

    private static string GenerateCMake(string root, string generated, LvglPreviewConfiguration configuration)
    {
        var lvgl = Relative(root, configuration.LvglDirectory);
        var header = PathBoundary.Resolve(root, configuration.ConfigurationHeader);
        var text = new StringBuilder("cmake_minimum_required(VERSION 3.20)\nproject(StudioXLvglPreview LANGUAGES C CXX)\nset(CMAKE_C_STANDARD 99)\nset(CMAKE_CXX_STANDARD 17)\n");
        text.AppendLine("file(GLOB_RECURSE LVGL_CORE " + Quote(Path.Combine(lvgl, "src", "*.c")) + ")");
        foreach (var exclusion in configuration.SourceExclusions ?? [])
        {
            text.AppendLine("list(REMOVE_ITEM LVGL_CORE " + Quote(PathBoundary.Resolve(lvgl, exclusion)) + ")");
        }
        text.AppendLine("add_executable(preview-next ${LVGL_CORE} " + Quote(Path.Combine(generated, "preview_host.c")));
        foreach (var source in configuration.SourceFiles)
        {
            text.AppendLine("  " + Quote(SourcePath(root, lvgl, source)));
        }
        text.AppendLine(")\nset_target_properties(preview-next PROPERTIES RUNTIME_OUTPUT_DIRECTORY \"${CMAKE_BINARY_DIR}/bin\")");
        text.AppendLine("target_include_directories(preview-next PRIVATE " + Quote(generated) + " " + Quote(Path.GetDirectoryName(header)!) + " " + Quote(lvgl) + " " + Quote(Path.Combine(lvgl, "demos")));
        foreach (var include in configuration.IncludeDirectories)
        {
            text.AppendLine("  " + Quote(SourcePath(root, lvgl, include)));
        }
        text.AppendLine(")");
        text.AppendLine("target_compile_definitions(preview-next PRIVATE LV_KCONFIG_IGNORE=1 " + Quote("LV_CONF_PATH=" + header.Replace('\\', '/')) + " STUDIOX_UI_ENTRY=" + configuration.EntryPoint
            + $" PREVIEW_WIDTH={configuration.Width} PREVIEW_HEIGHT={configuration.Height} PREVIEW_DRAW_ROWS={configuration.DrawBufferRows} PREVIEW_DRAW_COUNT={configuration.DrawBufferCount} PREVIEW_ZOOM={configuration.Zoom} PREVIEW_COLOR_DEPTH={configuration.ColorDepth})");
        text.AppendLine("target_compile_options(preview-next PRIVATE -O2 -Wall -Wextra)\ntarget_link_options(preview-next PRIVATE -static -static-libgcc)\ntarget_link_libraries(preview-next PRIVATE user32 gdi32)");
        return text.ToString();
    }

    public static void Validate(string root, LvglPreviewConfiguration configuration)
    {
        ValidateRequiredFields(configuration);
        if (configuration.FormatVersion != 1 || configuration.Width is < 16 or > 2048 || configuration.Height is < 16 or > 2048 ||
            configuration.DrawBufferRows < 1 || configuration.DrawBufferRows > configuration.Height || configuration.DrawBufferCount is < 1 or > 2 ||
            configuration.Zoom is < 1 or > 4 || configuration.ColorDepth is not (16 or 32) || configuration.TargetFramesPerSecond is < 1 or > 240 ||
            configuration.DisplayBandwidthBytesPerSecond is <= 0 or > 1_000_000_000_000 ||
            configuration.DisplayBandwidthSource?.Length > 500 || configuration.DisplayBandwidthSource is not null && configuration.DisplayBandwidthBytesPerSecond is null ||
            !Regex.IsMatch(configuration.EntryPoint ?? "", "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant))
        {
            throw new StudioXException("LVGL_CONFIGURATION", "预览配置无效；首版支持 16/32 位颜色、1/2 个绘制缓冲及 1–4 倍缩放。");
        }
        var lvgl = Relative(root, configuration.LvglDirectory);
        if (!Directory.Exists(Path.Combine(lvgl, "src")) || !File.Exists(Path.Combine(lvgl, "lvgl.h")))
        {
            throw new StudioXException("LVGL_LIBRARY", "共享 LVGL 目录缺少 src 或 lvgl.h。");
        }
        var evidence = LvglProjectInspector.ReadLibrary(root, lvgl, false, CancellationToken.None);
        if (!evidence.IsSupported)
        {
            throw new StudioXException("LVGL_VERSION", "预览支持具有明确版本信息的 LVGL 8.3.x；当前版本：" + (evidence.Version ?? "未知") + ". " + string.Join(" ", evidence.Diagnostics.Select(d => d.Message)));
        }
        if (!evidence.IsComplete)
        {
            throw new StudioXException("LVGL_LIBRARY_INCOMPLETE", "LVGL 核心文件不完整：" + string.Join("、", evidence.MissingFiles));
        }
        if (!File.Exists(PathBoundary.Resolve(root, configuration.ConfigurationHeader)))
        {
            throw new StudioXException("LVGL_CONFIGURATION", "LVGL 配置头不存在。");
        }
        if (configuration.SourceFiles.Select(source => SourcePath(root, lvgl, source)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.SourceFiles.Count)
        {
            throw new StudioXException("LVGL_DUPLICATE_SOURCE", "预览源文件列表包含重复路径。");
        }
        foreach (var source in configuration.SourceFiles)
        {
            var path = SourcePath(root, lvgl, source);
            if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx"))
            {
                throw new StudioXException("LVGL_SOURCE", "预览源文件不存在或不是 C/C++：" + source);
            }
            RejectOtherLibrary(lvgl, path);
            if (Contained(Path.Combine(lvgl, "src"), path) && !(configuration.SourceExclusions ?? []).Any(exclusion => PathBoundary.Resolve(lvgl, exclusion).Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                throw new StudioXException("LVGL_DUPLICATE_CORE", "LVGL 核心自动编译，不应再次加入 sourceFiles：" + source);
            }
        }
        foreach (var include in configuration.IncludeDirectories)
        {
            var path = SourcePath(root, lvgl, include);
            if (!Directory.Exists(path))
            {
                throw new StudioXException("LVGL_INCLUDE", "预览包含目录不存在：" + include);
            }
            RejectOtherLibrary(lvgl, path);
        }
        var includePaths = new[] { Path.GetDirectoryName(PathBoundary.Resolve(root, configuration.ConfigurationHeader))!, lvgl, Path.Combine(lvgl, "demos") }
            .Concat(configuration.IncludeDirectories.Select(include => SourcePath(root, lvgl, include))).ToArray();
        ValidateHeaderOrigins(root, lvgl, configuration.SourceFiles.Select(source => SourcePath(root, lvgl, source)).Append(PathBoundary.Resolve(root, configuration.ConfigurationHeader)), includePaths);
        foreach (var exclusion in configuration.SourceExclusions ?? [])
        {
            PathBoundary.Resolve(lvgl, exclusion);
        }
        if (configuration.UiDirectory is not null)
        {
            var ui = Relative(root, configuration.UiDirectory);
            if (!Contained(root, ui) || !Directory.Exists(ui))
            {
                throw new StudioXException("LVGL_UI_PATH", "UI 目录必须存在并位于当前工程。");
            }
        }
        foreach (var resource in configuration.ResourceDirectories ?? [])
        {
            var path = PathBoundary.Resolve(root, resource);
            if (!Directory.Exists(path))
            {
                throw new StudioXException("LVGL_RESOURCE_MISSING", "资源目录不存在：" + resource);
            }
            if (resource.Split('/').Any(LvglProjectInspector.IsExcludedDirectory) || LvglProjectInspector.IsLibraryRoot(path))
            {
                throw new StudioXException("LVGL_RESOURCE_PATH", "资源目录不能是构建缓存或整棵 LVGL 库：" + resource);
            }
        }
    }

    internal static void ValidateRequiredFields(LvglPreviewConfiguration configuration)
    {
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.LvglDirectory) ||
            string.IsNullOrWhiteSpace(configuration.ConfigurationHeader) || string.IsNullOrWhiteSpace(configuration.EntryPoint) ||
            configuration.SourceFiles is null || configuration.IncludeDirectories is null)
        {
            throw new StudioXException("LVGL_CONFIGURATION_FIELDS", "预览配置需要明确填写 lvglDirectory、configurationHeader、entryPoint、sourceFiles 和 includeDirectories；列表允许为空，但不能缺失或为 null。");
        }
    }

    private static void RejectOtherLibrary(string selected, string path)
    {
        var actual = LvglProjectInspector.FindLibraryAncestor(path);
        if (actual is not null && !actual.Equals(selected, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("LVGL_MULTIPLE_LIBRARIES", "源文件或包含目录来自另一份 LVGL，不能与所选库混编：" + path);
        }
    }
    private static void ValidateHeaderOrigins(string project, string selected, IEnumerable<string> roots, IReadOnlyList<string> includes)
    {
        var pending = new Stack<string>(roots);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var file = pending.Pop();
            if (!visited.Add(file) || new FileInfo(file).Length > 4 * 1024 * 1024)
            {
                continue;
            }
            foreach (Match include in Regex.Matches(LvglProjectInspector.StripComments(File.ReadAllText(file)), "(?m)^\\s*#\\s*include\\s*[\"<](?<file>[^\">]+)[\">]", RegexOptions.CultureInvariant))
            {
                var relative = include.Groups["file"].Value;
                if (Path.IsPathRooted(relative))
                {
                    continue;
                }
                var header = new[] { Path.GetDirectoryName(file)! }.Concat(includes)
                    .Select(directory => Path.GetFullPath(Path.Combine(directory, relative))).FirstOrDefault(File.Exists);
                if (header is null || !Contained(project, header) && !Contained(selected, header))
                {
                    continue;
                }
                LvglProjectInspector.ResolveRelative(project, LvglProjectInspector.Relative(project, header));
                RejectOtherLibrary(selected, header);
                if (!Contained(selected, header))
                {
                    pending.Push(header);
                }
            }
        }
    }
    private static async Task CopyResourcesAsync(string project, string relative, string destination, CancellationToken token)
    {
        var root = PathBoundary.Resolve(project, relative);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            if (LvglProjectInspector.IsLibraryRoot(directory))
            {
                throw new StudioXException("LVGL_RESOURCE_LIBRARY", "资源目录包含整棵库，请只选择实际资源子目录。");
            }
            foreach (var file in Directory.GetFiles(directory))
            {
                var resource = Path.GetRelativePath(project, file).Replace('\\', '/');
                var verified = PathBoundary.Resolve(project, resource);
                var target = PathBoundary.Resolve(destination, resource);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = File.OpenRead(verified);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(output, token);
            }
            foreach (var child in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (LvglProjectInspector.IsExcludedDirectory(name))
                {
                    continue;
                }
                PathBoundary.Resolve(project, Path.GetRelativePath(project, child).Replace('\\', '/'));
                pending.Push(child);
            }
        }
    }

    private static string SourcePath(string root, string lvgl, string relative)
    {
        var path = Relative(root, relative);
        if (!Contained(root, path) && !Contained(lvgl, path))
        {
            throw new StudioXException("LVGL_SOURCE_PATH", "预览源文件或包含目录必须位于当前工程或已配置的共享 LVGL 目录：" + relative);
        }
        return path;
    }
    private static bool Contained(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Relative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Any(c => c < 32 || ":\"|?*;$[]".Contains(c)))
        {
            throw new StudioXException("LVGL_PATH", "预览路径必须是可移植的相对路径：" + relative);
        }
        var path = Path.GetFullPath(Path.Combine(root, relative));
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("LVGL_PATH_LINK", "预览目录不能包含重解析点：" + relative);
            }
        }
        return path;
    }
    private static string Quote(string value) => "\"" + value.Replace('\\', '/').Replace("$", "\\$", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static Dictionary<string, string> CompilerEnvironment(string gcc, ResolvedToolset? tools = null)
    {
        var directories = new List<string> { Path.GetDirectoryName(gcc)! };
        if (tools is not null)
        {
            directories.AddRange(new[] { Path.GetDirectoryName(tools.Tool("cmake"))!, Path.GetDirectoryName(tools.Tool("ninja"))! });
        }
        directories.Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        return new()
        {
            ["PATH"] = string.Join(Path.PathSeparator, directories.Distinct(StringComparer.OrdinalIgnoreCase)),
            ["LANG"] = "C"
        };
    }
    private static async Task WriteChangedAsync(string path, string content, CancellationToken token)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, token) == content)
        {
            return;
        }
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), token);
    }
}
