namespace StudioX.Engine;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>工程级编译覆盖项；默认不改变模板、CMake 或预设中的参数。</summary>
public sealed record ProjectBuildSettings(int FormatVersion = 1,
    CompilerOptimization Optimization = CompilerOptimization.ProjectDefault,
    CompilerDebugInfo DebugInfo = CompilerDebugInfo.ProjectDefault,
    int? CodeRomSizeBytes = null, bool Mon51Profile = false,
    SdccMemoryModel MemoryModel = SdccMemoryModel.ProjectDefault,
    int? IdataSizeBytes = null, int? XdataSizeBytes = null)
{
    public const string RelativePath = ".studiox/build.json";
    [JsonIgnore] public bool HasOverrides => Optimization != CompilerOptimization.ProjectDefault || DebugInfo != CompilerDebugInfo.ProjectDefault || CodeRomSizeBytes is not null || Mon51Profile || MemoryModel != SdccMemoryModel.ProjectDefault || IdataSizeBytes is not null || XdataSizeBytes is not null;
    [JsonIgnore] public string? OptimizationFlag => Optimization == CompilerOptimization.ProjectDefault ? null : "-" + Optimization;
    [JsonIgnore] public string? DebugFlag => DebugInfo switch { CompilerDebugInfo.None => "-g0", CompilerDebugInfo.Standard => "-g2", CompilerDebugInfo.Full => "-g3", _ => null };
    [JsonIgnore] public string[] Flags => new[] { OptimizationFlag, DebugFlag }.OfType<string>().ToArray();
    [JsonIgnore] public string Summary => HasOverrides ? string.Join(' ', Flags) : "沿用工程默认";

    public void Validate()
    {
        if (FormatVersion != 1 || !Enum.IsDefined(Optimization) || !Enum.IsDefined(DebugInfo) || !Enum.IsDefined(MemoryModel))
        {
            throw new StudioXException("BUILD_SETTINGS", "不支持的编译参数配置，请检查 .studiox/build.json。");
        }
        if (CodeRomSizeBytes is < 1024)
        {
            throw new StudioXException("BUILD_SETTINGS", "代码 ROM 大小至少为 1024 字节。");
        }
        if (IdataSizeBytes is < 1 or > 256 || XdataSizeBytes is < 0 or > 65536)
        {
            throw new StudioXException("BUILD_SETTINGS", "SDCC 内部 RAM 容量须为 1–256 字节，XDATA 须为 0–65536 字节；XDATA 为 0 时不允许分配该存储区。");
        }
    }

    public void ValidateFor(ProjectManifest project)
    {
        Validate();
        if (MemoryModel != SdccMemoryModel.ProjectDefault && project.ToolsetId != "stc.sdcc")
        {
            throw new StudioXException("BUILD_SETTINGS", "SDCC 内存模型仅适用于 STC 8 位 SDCC 工程。");
        }
        if (project.ToolsetId != "stc.sdcc" && (IdataSizeBytes is not null || XdataSizeBytes is not null))
        {
            throw new StudioXException("BUILD_SETTINGS", "内部 RAM / XDATA 容量选项仅用于 STC 8 位 SDCC 工程。");
        }
        if (project.ToolsetId == "stc.sdcc" && project.TemplateId == "rt51" && MemoryModel is SdccMemoryModel.Small or SdccMemoryModel.Medium)
        {
            // 这是当前包内 rt51.h 的显式 ABI 契约，不能泛化为 SDCC --stack-auto 的限制。
            throw new StudioXException("BUILD_SETTINGS", "当前 RT51 模板要求所有编译单元使用 large 内存模型；请选择沿用工程或 large。");
        }
        if (Mon51Profile && (project.ToolsetId != "stc.sdcc" || !StcMon51Targets.Supports(project.DeviceId) || DebugInfo != CompilerDebugInfo.Standard || CodeRomSizeBytes > 0xdbfd))
        {
            throw new StudioXException("BUILD_SETTINGS", "Mon51 V2.5 构建预设仅适用于 " + StcMon51Targets.DisplayNames + "，须启用标准 CDB；用户 CODE 上限不能超过 56317 字节。");
        }
        if (project.ToolsetId == "stc.sdcc" && (DebugInfo == CompilerDebugInfo.Full ||
            Optimization is not (CompilerOptimization.ProjectDefault or CompilerOptimization.O0 or CompilerOptimization.Os or CompilerOptimization.O2)))
        {
            throw new StudioXException("BUILD_SETTINGS", "STC SDCC 支持默认、低优化、体积优先、速度优先及标准 CDB 调试信息；不支持 GCC 宏调试档位。");
        }
        if (project.ToolsetId != "stc.sdcc" && CodeRomSizeBytes is not null)
        {
            throw new StudioXException("BUILD_SETTINGS", "代码 ROM 大小选项仅用于 STC SDCC 工程。");
        }
    }

    private string[] StcOptimizationFlags => Optimization switch
    {
        CompilerOptimization.ProjectDefault => [],
        // SDCC 没有 GCC -O0 等价开关；此档仅关闭列出的优化过程。
        CompilerOptimization.O0 => ["--no-peep", "--nogcse", "--noinvariant", "--noinduction", "--noloopreverse", "--nolabelopt", "--nolospre"],
        CompilerOptimization.Os => ["--opt-code-size"],
        CompilerOptimization.O2 => ["--opt-code-speed"],
        _ => throw new StudioXException("BUILD_SETTINGS", "不支持的 STC SDCC 优化档位。")
    };
    internal string[] FlagsFor(ProjectManifest project) => project.ToolsetId == "stc.sdcc"
        ? StcOptimizationFlags.Concat(DebugInfo == CompilerDebugInfo.Standard ? new[] { "--debug" } : []).Concat(Mon51Profile ? new[] { "-DSTUDIOX_MON51_DEBUG=1" } : [])
            .Concat(SdccBuildOptions.Flag(MemoryModel) is { } model ? new[] { model } : []).ToArray() : Flags;

    internal string SummaryFor(ProjectManifest project) => project.ToolsetId == "stc.sdcc"
        ? (Optimization switch
        {
            CompilerOptimization.ProjectDefault => "SDCC 默认（平衡）",
            CompilerOptimization.O0 => "SDCC 低优化（关闭选定优化）",
            CompilerOptimization.Os => "SDCC 体积优先（--opt-code-size）",
            CompilerOptimization.O2 => "SDCC 速度优先（--opt-code-speed）",
            _ => throw new StudioXException("BUILD_SETTINGS", "不支持的 STC SDCC 优化档位。")
        }) + (DebugInfo == CompilerDebugInfo.Standard ? "；标准 CDB（--debug）" : "")
        + (MemoryModel == SdccMemoryModel.ProjectDefault ? "；内存模型沿用工程" : "；内存模型 " + MemoryModel.ToString().ToLowerInvariant())
        + (IdataSizeBytes is { } idata ? $"；内部 RAM 上限 {idata} 字节" : "")
        + (XdataSizeBytes is { } xdata ? $"；XDATA 上限 {xdata} 字节" : "")
        + (Mon51Profile ? "；Mon51 用户 CODE ≤56317 字节 / XDATA ≤1024 字节" : CodeRomSizeBytes is { } bytes ? $"；代码 ROM 上限 {bytes} 字节" : "；代码 ROM 沿用器件包上限")
        : Summary;

    public static async Task<ProjectBuildSettings> ReadAsync(string root, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(root, RelativePath);
        var value = File.Exists(path) ? await JsonStore.ReadAsync<ProjectBuildSettings>(path, token) : new();
        value.Validate();
        return value;
    }

    internal async Task<string> PrepareCMakeAsync(string build, ProjectManifest project, CancellationToken token, int? stcClockHz = null, int? stcClockDivider = null)
    {
        var hook = Path.Combine(build, "studiox-compiler-options.cmake").Replace('\\', '/');
        if (hook.Contains(';') || hook.Contains("]]", StringComparison.Ordinal))
        {
            throw new StudioXException("BUILD_SETTINGS_PATH", "编译参数配置不支持工程路径中的分号或连续右方括号。");
        }
        var flags = string.Join(' ', stcClockHz is { } hz
            ? FlagsFor(project).Append("-DSTUDIOX_CLOCK_HZ=" + hz.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL")
            : FlagsFor(project));
        if (stcClockDivider is { } divider)
        {
            flags += " -DSTUDIOX_CLOCK_DIVIDER=" + divider.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var linkFlags = project.ToolsetId == "stc.sdcc" ? string.Join(' ', new[]
        {
            Mon51Profile ? "--code-size " + Math.Min(CodeRomSizeBytes ?? 0xdbfd, 0xdbfd).ToString(System.Globalization.CultureInfo.InvariantCulture) : CodeRomSizeBytes is { } size ? "--code-size " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            DebugInfo == CompilerDebugInfo.Standard ? "--debug" : "",
            SdccBuildOptions.Flag(MemoryModel) ?? "",
            IdataSizeBytes is { } iram ? "--iram-size " + iram.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
            Mon51Profile || XdataSizeBytes is not null ? "--xram-size " + (await StcRamLimits.ReadAsync(Path.GetDirectoryName(build)!, project, token))!
                .EffectiveXdataSize(this).ToString(System.Globalization.CultureInfo.InvariantCulture) : ""
        }.Where(s => s.Length > 0)) : "";
        // <FLAGS> 包含目录、目标、接口库及源文件选项；在其后追加才能覆盖模板中的 -Os/-Og。
        // 不重写工程 CMake、预设或 SDK；对特殊自定义规则在生成后检查实际编译命令。
        var script = SdccBuildOptions.CMakeOverrides(this) + $$$"""
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

    internal async Task VerifyCommandsAsync(string build, ProjectManifest project, CancellationToken token, int? stcClockHz = null, int? stcClockDivider = null)
    {
        if (!HasOverrides && stcClockHz is null)
        {
            return;
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(build, "compile_commands.json"), token));
        var count = 0;
        foreach (var command in document.RootElement.EnumerateArray())
        {
            var file = command.GetProperty("file").GetString()!;
            if (Path.GetExtension(file).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx" or ".c++" or ".cp" or ".s" or ".asm" or ".sx"))
            {
                continue;
            }
            var arguments = command.TryGetProperty("arguments", out var list)
                ? list.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : SplitCommand(command.GetProperty("command").GetString()!);
            if (project.ToolsetId == "stc.sdcc")
            {
                var flags = FlagsFor(project);
                if (flags.Any(flag => !arguments.Contains(flag, StringComparer.Ordinal)) ||
                    Optimization is CompilerOptimization.Os or CompilerOptimization.O2 &&
                    arguments.LastOrDefault(arg => arg is "--opt-code-size" or "--opt-code-speed") != flags.First(arg => arg is "--opt-code-size" or "--opt-code-speed"))
                {
                    throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "SDCC 编译命令未应用所选优化档位：" + file);
                }
                if (stcClockHz is { } hz && arguments.LastOrDefault(arg => arg.StartsWith("-DSTUDIOX_CLOCK_HZ=", StringComparison.Ordinal)) !=
                    "-DSTUDIOX_CLOCK_HZ=" + hz.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL")
                {
                    throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "SDCC 编译命令未应用工程时钟宏：" + file);
                }
                if (stcClockDivider is { } divider && arguments.LastOrDefault(arg => arg.StartsWith("-DSTUDIOX_CLOCK_DIVIDER=", StringComparison.Ordinal)) !=
                    "-DSTUDIOX_CLOCK_DIVIDER=" + divider.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "SDCC 编译命令未应用托管系统分频：" + file);
                }
            }
            else if (OptimizationFlag is { } optimization && arguments.LastOrDefault(arg => Regex.IsMatch(arg, @"^-O(?:[0-3sgz]|fast)?$")) != optimization ||
                DebugFlag is { } debug && arguments.LastOrDefault(arg => Regex.IsMatch(arg, @"^-g(?:[0-3]|gdb[0-3]?)?$")) != debug)
            {
                throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "工程自定义编译规则覆盖了所选参数，未开始编译。请恢复工程默认或检查 CMake：" + file);
            }
            count++;
        }
        if (count == 0)
        {
            throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "没有找到可核对编译参数的 C/C++/汇编命令。");
        }
    }

    private static string[] SplitCommand(string command) => Regex.Matches(command, "(?:[^\\s\"']+|\"[^\"]*\"|'[^']*')+")
        .Select(match => match.Value.Trim('"', '\'')).ToArray();
}
