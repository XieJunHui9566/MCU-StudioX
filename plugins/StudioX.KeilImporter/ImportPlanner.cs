namespace StudioX.KeilImporter;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>预览锁定输入字节、选定型号和完整目标路径；预览不写文件。</summary>
public static partial class ImportPlanner
{
    public static async Task<ImportPreview> PlanAsync(ImportRequest request, CancellationToken token)
    {
        var root = ImportPaths.Absolute(request.SourceRoot);
        var projectFile = ImportPaths.Absolute(request.ProjectFile);
        var parent = ImportPaths.Absolute(request.ParentDirectory);
        var cli = ImportPaths.Absolute(request.CliPath);
        var repository = ImportPaths.Absolute(request.PackRepository);
        var destination = Path.Combine(parent, ImportPaths.ProjectName(request.ProjectName));
        if (!Directory.Exists(root) || !Directory.Exists(parent) || !ImportPaths.Within(root, projectFile))
        {
            throw new InvalidOperationException("源码根目录和创建位置必须存在，Keil 工程必须位于源码根目录中。");
        }
        if (Path.Exists(destination) || ImportPaths.Within(root, destination) || ImportPaths.Within(destination, root))
        {
            throw new InvalidOperationException("新工程目录必须不存在，且不能与原源码根目录重叠。");
        }
        if (!File.Exists(cli) || !Path.GetFileName(cli).Equals("StudioX.Cli.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("请指定当前已安装 IDE runtime/mcp-host/StudioX.Cli.exe，不运行 Keil 工程中的命令。");
        }
        var expectedPack = Path.Combine(repository, request.Device.PackId, request.Device.PackVersion);
        if (!ImportPaths.Absolute(expectedPack).Equals(request.Device.PackDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("器件选择与包目录不一致，请重新搜索。");
        }
        request = request with { SourceRoot = root, ProjectFile = projectFile, ParentDirectory = parent, CliPath = cli, PackRepository = repository };
        var target = KeilProjectReader.Read(projectFile).SingleOrDefault(target => target.Name == request.TargetName)
            ?? throw new InvalidOperationException("请选择一个明确的 Keil Target。");
        await DeviceCatalog.VerifyAsync(request.Device, token);
        var template = request.Device.Device.GetProperty("templates").EnumerateArray()
            .SingleOrDefault(template => template.GetProperty("id").GetString() == request.TemplateId);
        if (template.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("请选择当前器件包提供的明确构建支持配置。");
        }
        var issues = target.Issues.ToList();
        var templateDefines = template.TryGetProperty("build", out var templateBuild)
            ? Strings(templateBuild, "defines") : [];
        var frameworkMacros = new[] { "USE_HAL_DRIVER", "USE_STDPERIPH_DRIVER", "USE_FULL_LL_DRIVER" };
        if (frameworkMacros.Any(macro => target.Defines.Contains(macro) &&
            frameworkMacros.Any(other => other != macro && templateDefines.Contains(other))))
        {
            issues.Add(new("error", "FRAMEWORK_MISMATCH", "所选模板与原工程的 HAL/SPL/LL 宏不一致，请选择对应厂商库模板。"));
        }
        var additionalDefines = templateDefines.Where(define => define.StartsWith("STM32", StringComparison.Ordinal) &&
            !target.Defines.Any(original => MacroName(original) == MacroName(define))).ToArray();
        if (additionalDefines.Length > 0)
        {
            if (target.Defines.Any(define => define.StartsWith("STM32F10X_", StringComparison.Ordinal)) &&
                additionalDefines.Any(define => define.StartsWith("STM32F10X_", StringComparison.Ordinal)))
            {
                issues.Add(new("error", "DENSITY_MISMATCH", "原工程 STM32F10X 密度宏与明确选定器件的模板不一致，请先核对型号与容量。"));
            }
            issues.Add(new("warning", "DEVICE_MACRO_ADDED", "从明确选定且经过校验的器件模板补充 Keil 可能隐式提供的器件宏：" + string.Join(", ", additionalDefines)));
        }
        if (!request.Device.Id.Equals(target.Device, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("error", "DEVICE_MISMATCH", "Keil Device（" + target.Device + "）与所选型号（" + request.Device.Id + "）不同。本版不自动跨型号移植，请选择完全一致的已安装型号。"));
        }
        foreach (var region in target.Memory.Where(region => region.Bytes > 0))
        {
            var originKey = region.Name == "IROM" ? "flashOrigin" : "ramOrigin";
            var bytesKey = region.Name == "IROM" ? "flashBytes" : "ramBytes";
            if (region.Origin != request.Device.Device.GetProperty(originKey).GetUInt32() ||
                region.Bytes != request.Device.Device.GetProperty(bytesKey).GetUInt32())
            {
                issues.Add(new("error", "MEMORY_LAYOUT", "Keil " + region.Name + " 地址/容量与器件包不一致；可能是 bootloader 或自定义内存布局，请先提供对应 GNU 链接配置。"));
            }
        }
        var directory = Path.GetDirectoryName(projectFile)!;
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applicationSources = new List<string>();
        var includeDirectories = new List<string>();
        var originalSystem = false;
        var visitedDirectories = 0;
        var visitedFiles = 0;
        // 按原工程布局复制资源，不将源码套进模板目录，也不按扩展名丢弃图片或配置。
        AddDirectory(root, recursive: true);
        AddFile(projectFile);
        foreach (var supportFile in target.SupportFiles)
        {
            var path = ImportPaths.ResolveSource(root, directory, supportFile);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("Target 中的支持文件缺失，请补齐依赖：" + supportFile);
            }
            AddFile(path);
        }
        foreach (var source in target.Sources)
        {
            token.ThrowIfCancellationRequested();
            var path = ImportPaths.ResolveSource(root, directory, source);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("源码缺失，请补齐原工程依赖：" + source);
            }
            AddFile(path);
            AddDirectory(Path.GetDirectoryName(path)!, recursive: false);
            var name = Path.GetFileName(path);
            if (name.StartsWith("startup_stm32", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(path).Equals(".s", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("warning", "STARTUP_REPLACED", "原启动汇编仅作参考副本，构建改用所选型号包的 GCC 启动文件。请检查自定义向量、栈和复位初始化：" + source));
                continue;
            }
            var relative = ImportPaths.Relative(root, path);
            applicationSources.Add(relative);
            originalSystem |= name.StartsWith("system_stm32", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".c", StringComparison.OrdinalIgnoreCase);
            if (new FileInfo(path).Length <= 4 * 1024 * 1024)
            {
                var text = await File.ReadAllTextAsync(path, token);
                if (Path.GetExtension(path).Equals(".s", StringComparison.OrdinalIgnoreCase) && ArmAssembly().IsMatch(text))
                {
                    issues.Add(new("error", "ARMASM", "含非启动 ARMASM 汇编，需要先转换为 GNU 汇编：" + source));
                }
                if (KeilExtension().IsMatch(text))
                {
                    issues.Add(new("error", "KEIL_EXTENSION", "含 GCC 无法直接迁移的 Keil 内嵌汇编或绝对地址/运行库写法，请手工适配：" + source));
                }
            }
        }
        foreach (var include in target.IncludeDirectories)
        {
            var path = ImportPaths.ResolveSource(root, directory, include);
            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException("头文件目录缺失，请补齐依赖：" + include);
            }
            AddDirectory(path, recursive: true);
            var relative = ImportPaths.Relative(root, path);
            includeDirectories.Add(relative);
        }
        var packSources = Strings(request.Device.Device, "sources")
            .Where(source => Path.GetFileName(source).StartsWith("startup", StringComparison.OrdinalIgnoreCase) ||
                source == "support/runtime.c" || !originalSystem && Path.GetFileName(source).StartsWith("system_stm32", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (packSources.Count(source => Path.GetFileName(source).StartsWith("startup", StringComparison.OrdinalIgnoreCase)) != 1)
        {
            issues.Add(new("error", "PACK_STARTUP", "所选器件包没有唯一的 GCC 启动文件，本版无法生成可用固件工程。"));
        }
        if (!originalSystem)
        {
            issues.Add(new("warning", "SYSTEM_FALLBACK", "原 Target 未包含 system_stm32*.c；将采用所选包的系统初始化。请核对时钟、外部晶振和向量表设置。"));
            if (target.Defines.Contains("USE_STDPERIPH_DRIVER"))
            {
                issues.Add(new("error", "SPL_SYSTEM_REQUIRED", "标准库工程需要在 Target 中显式包含原 system_stm32*.c；不能将 HAL 的系统头文件混入原 SPL。请先补齐源码副本中的系统文件。"));
            }
        }
        var files = new List<CopyInput>();
        long total = 0;
        foreach (var (relative, path) in inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var size = new FileInfo(path).Length;
            total = checked(total + size);
            if (size > 16 * 1024 * 1024 || total > 256 * 1024 * 1024)
            {
                throw new InvalidOperationException("复制资源超过单文件 16 MiB 或总计 256 MiB，本版需要先精简源码目录。");
            }
            files.Add(new(path, relative, size, await ImportPaths.HashAsync(path, token)));
        }
        var sources = applicationSources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var includes = includeDirectories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var file in files.Where(file => file.RelativePath.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.Equals("KEIL-MIGRATION.md", StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.Equals(".studiox", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Equals("device", StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.StartsWith(".studiox/", StringComparison.OrdinalIgnoreCase) ||
            file.RelativePath.StartsWith("device/", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new("error", "OUTPUT_COLLISION", "原工程文件与 StudioX 构建支持路径冲突，不会覆盖。请整理一份副本后重试：" + file.RelativePath));
        }
        var edits = await SourceCompatibility.PlanAsync(files.ToArray(), sources, issues, token);
        if (edits.Length > 64)
        {
            throw new InvalidOperationException("源码修正超过 64 项，请拆分 Target 后逐项核对。");
        }
        var cmake = ImportedCMake.Render(request, target, sources, includes, packSources, additionalDefines);
        if (cmake.Length > 120000)
        {
            throw new InvalidOperationException("生成配置超过本版面板预览容量，请拆分 Target 或先手工整理构建输入。");
        }
        var cliHash = await ImportPaths.HashAsync(cli, token);
        var previewId = ImportPaths.HashText(JsonSerializer.Serialize(new { request.ProjectName, destination, target.Name, device = request.Device.Key,
            request.Device.ContentHash, request.TemplateId, files, edits, cmake, cliHash }, DeviceCatalog.Json));
        return new(request, destination, target, files.ToArray(), sources, includes, packSources, additionalDefines, edits, issues.ToArray(), cmake, cliHash, previewId);

        void AddFile(string path)
        {
            ImportPaths.RejectLinks(path);
            var relative = ImportPaths.Relative(root, path);
            if (inputs.TryGetValue(relative, out var previous) && !previous.Equals(path, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("源码路径存在大小写冲突：" + relative);
            }
            inputs[relative] = path;
            if (inputs.Count > 6000)
            {
                throw new InvalidOperationException("复制文件超过 6000 项，请缩小 IncludePath 或源码根目录。");
            }
        }

        void AddDirectory(string path, bool recursive, int depth = 0)
        {
            token.ThrowIfCancellationRequested();
            if (++visitedDirectories > 4096 || depth > 32)
            {
                throw new InvalidOperationException("头文件目录过多或超过 32 层，请缩小 IncludePath。");
            }
            ImportPaths.RejectLinks(path);
            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (++visitedFiles > 20000)
                {
                    throw new InvalidOperationException("扫描文件超过 20000 项，请缩小 IncludePath。");
                }
                AddFile(file);
            }
            if (recursive)
            {
                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    ImportPaths.RejectLinks(child);
                    if (Path.GetFileName(child).ToLowerInvariant() is not (".git" or ".build" or "build" or "objects" or "listings" or "debug" or "release" or "obj" or "bin"))
                    {
                        AddDirectory(child, recursive: true, depth + 1);
                    }
                }
            }
        }
    }

    public static string[] Strings(JsonElement value, string name) => value.TryGetProperty(name, out var array)
        ? array.EnumerateArray().Select(element => element.GetString()!).ToArray() : [];

    public static string MacroName(string define) => define.Split('=')[0];

    [GeneratedRegex(@"(?im)^\s*(AREA|EXPORT|IMPORT|PROC|ENDP|PRESERVE8|THUMB)\b")]
    private static partial Regex ArmAssembly();

    [GeneratedRegex(@"(?m)\b__asm\s*\{|\b__asm\s+\w+\s+\w+\s*\(|\b__attribute__\s*\(\(\s*at\s*\(|^\s*#pragma\s+(import|arm section)\b")]
    private static partial Regex KeilExtension();
}
