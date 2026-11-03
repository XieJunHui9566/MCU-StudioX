namespace StudioX.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>对 SDCC 的互斥内存模型做替换和生成后校验，避免多个开关的优先级改变 ABI。</summary>
internal static class SdccBuildOptions
{
    internal static string? Flag(SdccMemoryModel model) => model switch
    {
        SdccMemoryModel.ProjectDefault => null,
        SdccMemoryModel.Small => "--model-small",
        SdccMemoryModel.Medium => "--model-medium",
        SdccMemoryModel.Large => "--model-large",
        _ => throw new StudioXException("BUILD_SETTINGS", "不支持的 SDCC 内存模型。")
    };

    internal static string CMakeOverrides(ProjectBuildSettings settings)
    {
        var filters = new List<string>();
        if (settings.MemoryModel != SdccMemoryModel.ProjectDefault)
        {
            filters.Add("list(FILTER _studiox_options EXCLUDE REGEX \"^--model-(small|medium|large)$\")");
        }
        if (settings.IdataSizeBytes is not null)
        {
            filters.Add("list(FILTER _studiox_options EXCLUDE REGEX \"^SHELL:--iram-size [0-9]+$\")");
        }
        if (settings.XdataSizeBytes is not null || settings.Mon51Profile)
        {
            filters.Add("list(FILTER _studiox_options EXCLUDE REGEX \"^SHELL:--xram-size [0-9]+$\")");
        }
        return filters.Count == 0 ? "" : $$"""
        # project() 的注入早于器件子目录；延后到生成前移除包接口中已覆盖的模型或容量。
        # 用户在其他目标/全局/源文件中自定义的选项不静默删除，由真实命令校验拒绝冲突。
        function(_studiox_sdcc_replace_device_model)
            if(TARGET studiox_device)
                foreach(_studiox_property INTERFACE_COMPILE_OPTIONS INTERFACE_LINK_OPTIONS)
                    get_target_property(_studiox_options studiox_device ${_studiox_property})
                    if(_studiox_options)
                        {{string.Join('\n', filters)}}
                        set_property(TARGET studiox_device PROPERTY ${_studiox_property} "${_studiox_options}")
                    endif()
                endforeach()
            endif()
        endfunction()
        cmake_language(DEFER CALL _studiox_sdcc_replace_device_model)

        """;
    }

    internal static async Task VerifyCommandsAsync(string build, ProjectBuildSettings settings, StcRamLimits ram, ResolvedToolset tools,
        IReadOnlyDictionary<string, string> environment, CancellationToken token)
    {
        var models = new HashSet<SdccMemoryModel>();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(build, "compile_commands.json"), token));
        var sources = 0;
        foreach (var command in document.RootElement.EnumerateArray())
        {
            var file = command.GetProperty("file").GetString()!;
            if (!Path.GetExtension(file).Equals(".c", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var arguments = command.TryGetProperty("arguments", out var list)
                ? list.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : SplitCommand(command.GetProperty("command").GetString()!);
            models.Add(ReadModel(arguments, settings.MemoryModel, file));
            sources++;
        }
        if (sources == 0 || models.Count != 1)
        {
            throw Conflict("工程的 C 编译单元必须使用同一 SDCC 内存模型。");
        }

        // Ninja 展开的是最终规则，覆盖自定义链接模板、全局 flags 与目标 link options。
        // 仅看 compile_commands 不能证明链接时选中了相同模型的 SDCC 运行库。
        var result = await new ProcessRunner().RunAsync(new(tools.Tool("ninja"), ["-C", build, "-t", "commands", "firmware.ihx"],
            build, TimeSpan.FromSeconds(30), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
        await File.WriteAllTextAsync(Path.Combine(build, "studiox-sdcc-commands.txt"), result.StandardOutput + result.StandardError, token);
        if (!result.Success || result.OutputTruncated)
        {
            throw new StudioXException("SDCC_MEMORY_MODEL_COMMANDS", "无法完整核对 Ninja 的 SDCC 编译和链接命令，请检查 studiox-sdcc-commands.txt。");
        }
        var compiler = tools.Tool("sdcc").Replace('\\', '/');
        var links = 0;
        var compiles = 0;
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Replace('\\', '/').Replace("\"", "").Contains(compiler, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Ninja 的 Windows 链接行外层包有 cmd /C 和嵌套引号；只解析已锁定编译器后的参数。
            var normalized = line.Replace('\\', '/');
            if (Regex.Matches(normalized, Regex.Escape(compiler), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count != 1)
            {
                throw new StudioXException("SDCC_MEMORY_MODEL_COMMANDS", "自定义 Ninja 命令在同一行包含多次 SDCC 调用，无法唯一核对编译和链接设置。");
            }
            var argumentsText = normalized[(normalized.IndexOf(compiler, StringComparison.OrdinalIgnoreCase) + compiler.Length)..].TrimStart('"');
            var arguments = SplitCommand(Regex.Split(argumentsText, @"\s+&&\s+", RegexOptions.CultureInvariant)[0]);
            var model = ReadModel(arguments, settings.MemoryModel, "Ninja 最终命令");
            if (!models.Contains(model))
            {
                throw Conflict("SDCC 最终编译/链接命令的内存模型不一致。");
            }
            if (arguments.Contains("-c", StringComparer.Ordinal))
            {
                compiles++;
            }
            else
            {
                ram.VerifyLinkArguments(arguments, settings);
                links++;
            }
        }
        if (compiles == 0 || links != 1)
        {
            throw new StudioXException("SDCC_MEMORY_MODEL_COMMANDS", "未找到唯一的 SDCC 固件链接及对应编译命令；不接受无法核对模型的自定义构建规则。");
        }
    }

    private static SdccMemoryModel ReadModel(string[] arguments, SdccMemoryModel selected, string source)
    {
        if (arguments.Any(argument => argument.StartsWith('@')))
        {
            throw new StudioXException("SDCC_MEMORY_MODEL_COMMANDS", "SDCC 内存模型无法从自定义响应文件命令中核对：" + source);
        }
        var flags = arguments.Where(argument => argument.StartsWith("--model", StringComparison.Ordinal)).ToArray();
        if (flags.Length > 1)
        {
            throw Conflict("SDCC 命令含多个互斥内存模型，请移除 CMake 自定义冲突选项：" + source);
        }
        var actual = flags.SingleOrDefault() switch
        {
            null => SdccMemoryModel.Small,
            "--model-small" => SdccMemoryModel.Small,
            "--model-medium" => SdccMemoryModel.Medium,
            "--model-large" => SdccMemoryModel.Large,
            _ => throw Conflict("STC 8 位工程只支持 small、medium、large 内存模型：" + source)
        };
        if (selected != SdccMemoryModel.ProjectDefault && (flags.Length != 1 || actual != selected))
        {
            throw Conflict("SDCC 命令未应用所选内存模型，请检查自定义 CMake 规则：" + source);
        }
        return actual;
    }

    private static StudioXException Conflict(string message) => new("SDCC_MEMORY_MODEL_CONFLICT", message);
    private static string[] SplitCommand(string command) => Regex.Matches(command, "(?:[^\\s\"']+|\"[^\"]*\"|'[^']*')+")
        .Select(match => match.Value.Trim('"', '\'')).ToArray();
}
