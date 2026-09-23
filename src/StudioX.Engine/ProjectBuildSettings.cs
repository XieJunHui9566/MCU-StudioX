namespace StudioX.Engine;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public enum CompilerOptimization { ProjectDefault, O0, Og, O1, O2, O3, Os }
public enum CompilerDebugInfo { ProjectDefault, None, Standard, Full }

/// <summary>工程级编译覆盖项；默认不改变模板、CMake 或预设中的参数。</summary>
public sealed record ProjectBuildSettings(int FormatVersion = 1,
    CompilerOptimization Optimization = CompilerOptimization.ProjectDefault,
    CompilerDebugInfo DebugInfo = CompilerDebugInfo.ProjectDefault,
    int? CodeRomSizeBytes = null)
{
    public const string RelativePath = ".studiox/build.json";
    [JsonIgnore] public bool HasOverrides => Optimization != CompilerOptimization.ProjectDefault || DebugInfo != CompilerDebugInfo.ProjectDefault || CodeRomSizeBytes is not null;
    [JsonIgnore] public string? OptimizationFlag => Optimization == CompilerOptimization.ProjectDefault ? null : "-" + Optimization;
    [JsonIgnore] public string? DebugFlag => DebugInfo switch { CompilerDebugInfo.None => "-g0", CompilerDebugInfo.Standard => "-g2", CompilerDebugInfo.Full => "-g3", _ => null };
    [JsonIgnore] public string[] Flags => new[] { OptimizationFlag, DebugFlag }.OfType<string>().ToArray();
    [JsonIgnore] public string Summary => HasOverrides ? string.Join(' ', Flags) : "沿用工程默认";

    public void Validate()
    {
        if (FormatVersion != 1 || !Enum.IsDefined(Optimization) || !Enum.IsDefined(DebugInfo))
            throw new StudioXException("BUILD_SETTINGS", "不支持的编译参数配置，请检查 .studiox/build.json。");
        if (CodeRomSizeBytes is < 1024)
            throw new StudioXException("BUILD_SETTINGS", "代码 ROM 大小至少为 1024 字节。");
    }

    public void ValidateFor(ProjectManifest project)
    {
        Validate();
        if (project.ToolsetId == "stc.sdcc" && (DebugInfo != CompilerDebugInfo.ProjectDefault ||
            Optimization is not (CompilerOptimization.ProjectDefault or CompilerOptimization.O0 or CompilerOptimization.Os or CompilerOptimization.O2)))
            throw new StudioXException("BUILD_SETTINGS", "STC SDCC 仅支持默认、低优化、体积优先与速度优先；当前不提供调试信息设置。");
        if (project.ToolsetId != "stc.sdcc" && CodeRomSizeBytes is not null)
            throw new StudioXException("BUILD_SETTINGS", "代码 ROM 大小选项仅用于 STC SDCC 工程。");
    }

    internal string[] FlagsFor(ProjectManifest project) => project.ToolsetId == "stc.sdcc" ? Optimization switch
    {
        CompilerOptimization.ProjectDefault => [],
        // SDCC 没有 GCC -O0 等价开关；此档仅关闭列出的优化过程。
        CompilerOptimization.O0 => ["--no-peep", "--nogcse", "--noinvariant", "--noinduction", "--noloopreverse", "--nolabelopt", "--nolospre"],
        CompilerOptimization.Os => ["--opt-code-size"],
        CompilerOptimization.O2 => ["--opt-code-speed"],
        _ => throw new StudioXException("BUILD_SETTINGS", "不支持的 STC SDCC 优化档位。")
    } : Flags;

    internal string SummaryFor(ProjectManifest project) => project.ToolsetId == "stc.sdcc"
        ? (Optimization switch
        {
            CompilerOptimization.ProjectDefault => "SDCC 默认（平衡）",
            CompilerOptimization.O0 => "SDCC 低优化（关闭选定优化）",
            CompilerOptimization.Os => "SDCC 体积优先（--opt-code-size）",
            CompilerOptimization.O2 => "SDCC 速度优先（--opt-code-speed）",
            _ => throw new StudioXException("BUILD_SETTINGS", "不支持的 STC SDCC 优化档位。")
        }) + (CodeRomSizeBytes is { } bytes ? $"；代码 ROM 上限 {bytes} 字节" : "；代码 ROM 沿用器件包上限")
        : Summary;

    public static async Task<ProjectBuildSettings> ReadAsync(string root, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(root, RelativePath);
        var value = File.Exists(path) ? await JsonStore.ReadAsync<ProjectBuildSettings>(path, token) : new();
        value.Validate(); return value;
    }

    internal async Task<string> PrepareCMakeAsync(string build, ProjectManifest project, CancellationToken token, int? stcClockHz = null)
    {
        var hook = Path.Combine(build, "studiox-compiler-options.cmake").Replace('\\', '/');
        if (hook.Contains(';') || hook.Contains("]]", StringComparison.Ordinal))
            throw new StudioXException("BUILD_SETTINGS_PATH", "编译参数配置不支持工程路径中的分号或连续右方括号。");
        var flags = string.Join(' ', stcClockHz is { } hz
            ? FlagsFor(project).Append("-DSTUDIOX_CLOCK_HZ=" + hz.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL")
            : FlagsFor(project));
        var linkFlags = project.ToolsetId == "stc.sdcc" && CodeRomSizeBytes is { } size
            ? "--code-size " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        // <FLAGS> 包含目录、目标、接口库及源文件选项；在其后追加才能覆盖模板中的 -Os/-Og。
        // 不重写工程 CMake、预设或 SDK；对特殊自定义规则在生成后检查实际编译命令。
        var script = $$$"""
            foreach(_studiox_lang C CXX ASM)
                set(_studiox_rule "CMAKE_${_studiox_lang}_COMPILE_OBJECT")
                if(DEFINED ${_studiox_rule})
                    string(FIND "${${_studiox_rule}}" "<FLAGS>" _studiox_at)
                    if(_studiox_at LESS 0)
                        message(FATAL_ERROR "StudioX: unsupported compile rule ${_studiox_rule}; use project-default compiler settings")
                    endif()
                    string(REPLACE "<FLAGS> {{{flags}}}" "<FLAGS>" ${_studiox_rule} "${${_studiox_rule}}")
                    string(REPLACE "<FLAGS>" "<FLAGS> {{{flags}}}" ${_studiox_rule} "${${_studiox_rule}}")
                endif()
                set(_studiox_link "CMAKE_${_studiox_lang}_LINK_EXECUTABLE")
                if(DEFINED ${_studiox_link})
                    if("{{{project.ToolsetId}}}" STREQUAL "stc.sdcc")
                        string(REPLACE "<LINK_FLAGS> {{{linkFlags}}}" "<LINK_FLAGS>" ${_studiox_link} "${${_studiox_link}}")
                        string(REPLACE "<LINK_FLAGS>" "<LINK_FLAGS> {{{linkFlags}}}" ${_studiox_link} "${${_studiox_link}}")
                    else()
                        string(REPLACE "<LINK_FLAGS> {{{flags}}}" "<LINK_FLAGS>" ${_studiox_link} "${${_studiox_link}}")
                        string(REPLACE "<LINK_FLAGS>" "<LINK_FLAGS> {{{flags}}}" ${_studiox_link} "${${_studiox_link}}")
                    endif()
                endif()
            endforeach()
            """;
        await File.WriteAllTextAsync(hook, script, token);
        var bootstrap = Path.Combine(build, "studiox-compiler-bootstrap.cmake");
        // -C 在预设参数之后读取，追加现有注入列表，不丢弃工程自带的 project include。
        await File.WriteAllTextAsync(bootstrap, $$"""
            set(_studiox_hook [[{{hook}}]])
            list(REMOVE_ITEM CMAKE_PROJECT_INCLUDE "${_studiox_hook}")
            list(APPEND CMAKE_PROJECT_INCLUDE "${_studiox_hook}")
            set(CMAKE_PROJECT_INCLUDE "${CMAKE_PROJECT_INCLUDE}" CACHE STRING "Project include files" FORCE)
            """, token);
        return bootstrap;
    }

    internal async Task VerifyCommandsAsync(string build, ProjectManifest project, CancellationToken token, int? stcClockHz = null)
    {
        if (!HasOverrides && stcClockHz is null) return;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(build, "compile_commands.json"), token));
        var count = 0;
        foreach (var command in document.RootElement.EnumerateArray())
        {
            var file = command.GetProperty("file").GetString()!;
            if (Path.GetExtension(file).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx" or ".c++" or ".cp" or ".s" or ".asm" or ".sx")) continue;
            var arguments = command.TryGetProperty("arguments", out var list)
                ? list.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : SplitCommand(command.GetProperty("command").GetString()!);
            if (project.ToolsetId == "stc.sdcc")
            {
                var flags = FlagsFor(project);
                if (flags.Any(flag => !arguments.Contains(flag, StringComparer.Ordinal)) ||
                    Optimization is CompilerOptimization.Os or CompilerOptimization.O2 &&
                    arguments.LastOrDefault(arg => arg is "--opt-code-size" or "--opt-code-speed") != flags[^1])
                    throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "SDCC 编译命令未应用所选优化档位：" + file);
                if (stcClockHz is { } hz && arguments.LastOrDefault(arg => arg.StartsWith("-DSTUDIOX_CLOCK_HZ=", StringComparison.Ordinal)) !=
                    "-DSTUDIOX_CLOCK_HZ=" + hz.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL")
                    throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "SDCC 编译命令未应用工程时钟宏：" + file);
            }
            else if (OptimizationFlag is { } optimization && arguments.LastOrDefault(arg => Regex.IsMatch(arg, @"^-O(?:[0-3sgz]|fast)?$")) != optimization ||
                DebugFlag is { } debug && arguments.LastOrDefault(arg => Regex.IsMatch(arg, @"^-g(?:[0-3]|gdb[0-3]?)?$")) != debug)
                throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "工程自定义编译规则覆盖了所选参数，未开始编译。请恢复工程默认或检查 CMake：" + file);
            count++;
        }
        if (count == 0) throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "没有找到可核对编译参数的 C/C++/汇编命令。");
    }

    private static string[] SplitCommand(string command) => Regex.Matches(command, "(?:[^\\s\"']+|\"[^\"]*\"|'[^']*')+")
        .Select(match => match.Value.Trim('"', '\'')).ToArray();
}
