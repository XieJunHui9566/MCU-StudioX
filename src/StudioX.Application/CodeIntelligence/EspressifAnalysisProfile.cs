namespace StudioX.Application.CodeIntelligence;

using StudioX.Engine;
using StudioX.Foundation;

/// <summary>把原生 SDK 参数转换成语言服务参数；转换结果不参与真实编译或 ABI 验证。</summary>
internal sealed record EspressifAnalysisProfile(string Target, string HeaderRoot, string[] Flags, string Description)
{
    public static bool IsXtensa(string target) => target is "esp32" or "esp32s3" or "esp8266";

    public static async Task<EspressifAnalysisProfile> ReadAsync(string runtime, string projectRoot,
        ProjectManifest project, CancellationToken token)
    {
        var settings = project.Espressif!;
        var root = PathBoundary.Resolve(runtime, $"toolsets/{project.ToolsetId}/{project.ToolsetVersion}");
        var manifest = await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(root, "toolset.json"), token).ConfigureAwait(false);
        if (manifest.FormatVersion != 1 || manifest.Id != project.ToolsetId || manifest.Version != settings.SdkVersion ||
            manifest.CompilerId != project.CompilerId || manifest.Purpose != settings.Framework)
        {
            throw new StudioXException("LANGUAGE_ESPRESSIF_RUNTIME", "SDK 头文件与工程锁定的 Espressif 工具集不一致。");
        }
        // 这里只读取头文件，不执行 GCC 或 SDK 脚本；所有资源路径仍由工具集边界约束。
        var tools = new ResolvedToolset(manifest, root, "").ForEspressifTarget(settings.Target);
        var compilerRoot = Path.GetDirectoryName(Path.GetDirectoryName(tools.Tool("gcc")))!;
        var targetTriple = settings.Target switch
        {
            "esp8266" => "xtensa-lx106-elf",
            "esp32" or "esp32s3" => "xtensa-esp-elf",
            _ => "riscv32-esp-elf"
        };
        var result = new List<string>
        {
            IsXtensa(settings.Target) ? "--target=i386-unknown-elf" : "--target=riscv32-unknown-elf",
            "-ffreestanding", "-ferror-limit=0",
            "-I" + Path.Combine(projectRoot, "main").Replace('\\', '/'),
            "-I" + Path.Combine(projectRoot, "src").Replace('\\', '/'),
            "-I" + Path.Combine(projectRoot, "include").Replace('\\', '/')
        };
        if (IsXtensa(settings.Target))
        {
            // LLVM clangd 没有 Xtensa 后端；这些标记只选择 SDK 声明分支，不代表验证了 Xtensa ABI。
            result.Add("-D__XTENSA__=1");
        }
        var includes = Path.Combine(compilerRoot, targetTriple, "include");
        if (Directory.Exists(includes))
        {
            result.AddRange(["-isystem", EspressifPathIdentity.NormalizePath(includes).Replace('\\', '/')]);
        }
        var description = IsXtensa(settings.Target)
            ? "Xtensa 使用通用 32 位 C/C++ 解析；目标 ABI 以 SDK 编译为准"
            : "RISC-V 使用原生 SDK 参数进行 32 位 C/C++ 解析；编译结果以 SDK 为准";
        return new(settings.Target, EspressifPathIdentity.NormalizePath(root), result.ToArray(), description);
    }

    public string[] Translate(string[] original, string directory, string file, string clangExecutable)
    {
        var translated = new List<string> { clangExecutable };
        for (var index = 1; index < original.Length; index++)
        {
            var argument = original[index];
            if (argument is "-D" or "-U" or "-x")
            {
                if (++index >= original.Length)
                {
                    throw new StudioXException("LANGUAGE_ESPRESSIF_COMMAND", "原生编译参数缺少值：" + argument);
                }
                translated.AddRange([argument, original[index]]);
            }
            else if (argument is "-I" or "-isystem" or "-iquote" or "-idirafter" or "-include" or "-imacros" or "-isysroot")
            {
                if (++index >= original.Length)
                {
                    throw new StudioXException("LANGUAGE_ESPRESSIF_COMMAND", "原生头文件参数缺少路径：" + argument);
                }
                translated.AddRange([argument, Normalize(original[index], directory)]);
            }
            else if (argument.StartsWith("-I", StringComparison.Ordinal) && argument.Length > 2)
            {
                translated.Add("-I" + Normalize(argument[2..], directory));
            }
            else if (argument.StartsWith("--sysroot=", StringComparison.Ordinal))
            {
                translated.Add("--sysroot=" + Normalize(argument[10..], directory));
            }
            else if (new[] { "-isystem", "-iquote", "-idirafter", "-include", "-imacros", "-isysroot" }
                .FirstOrDefault(prefix => argument.StartsWith(prefix, StringComparison.Ordinal) && argument.Length > prefix.Length) is { } includePrefix)
            {
                translated.AddRange([includePrefix, Normalize(argument[includePrefix.Length..], directory)]);
            }
            else if (argument.StartsWith("-D", StringComparison.Ordinal) || argument.StartsWith("-U", StringComparison.Ordinal) ||
                argument.StartsWith("-std=", StringComparison.Ordinal) ||
                argument is "-fexceptions" or "-fno-exceptions" or "-frtti" or "-fno-rtti" or "-funsigned-char" or "-fsigned-char" or
                    "-fshort-enums" or "-fshort-wchar" or "-nostdinc" or "-nostdinc++" or "-pthread")
            {
                translated.Add(argument);
            }
            else if (!IsXtensa(Target) && argument.StartsWith("-march=", StringComparison.Ordinal))
            {
                // Espressif 的厂商 ISA 扩展不在通用 LLVM 中；标准 ISA 仍按真实编译参数保留。
                translated.Add(string.Join('_', argument.Split('_').Where(part => !part.StartsWith('x'))));
            }
            else if (!IsXtensa(Target) && argument.StartsWith("-mabi=", StringComparison.Ordinal))
            {
                translated.Add(argument);
            }
        }
        // 链接、依赖文件、优化和 GCC 专用机器选项不能让 clangd 退出；原始数据库保持不变。
        translated.AddRange(Flags);
        translated.Add(file);
        return translated.ToArray();
    }

    private static string Normalize(string value, string directory) =>
        EspressifPathIdentity.NormalizePath(Path.GetFullPath(value, directory)).Replace('\\', '/');
}
