namespace StudioX.KeilImporter;

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

/// <summary>只解释明确的 ARMADS XML；构建事件和工程文件中的文本均不作为指令执行。</summary>
public static partial class KeilProjectReader
{
    public static KeilTarget[] Read(string projectFile)
    {
        projectFile = ImportPaths.Absolute(projectFile);
        if (!File.Exists(projectFile) || Path.GetExtension(projectFile).ToLowerInvariant() is not (".uvprojx" or ".uvproj") ||
            new FileInfo(projectFile).Length > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException("请选择不超过 4 MiB 的 Keil ARM 工程 .uvprojx / .uvproj。");
        }
        using var stream = File.OpenRead(projectFile);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "Project")
        {
            throw new InvalidOperationException("不是受支持的 Keil Project XML；不接受工作区或工程选项文件。");
        }
        var targets = document.Root.Element("Targets")?.Elements("Target").ToArray() ?? [];
        if (targets.Length is < 1 or > 64)
        {
            throw new InvalidOperationException("工程没有可用 Target 或数量超过 64。");
        }
        var names = targets.Select(target => Text(target, "TargetName")).ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new InvalidOperationException("Target 名称为空或重复，无法可靠选择。");
        }
        return targets.Select(ParseTarget).ToArray();
    }

    private static KeilTarget ParseTarget(XElement target)
    {
        var issues = new List<ImportIssue>();
        var options = target.Element("TargetOption");
        var common = options?.Element("TargetCommonOption");
        var arm = options?.Element("TargetArmAds");
        var memory = new List<KeilMemory>();
        if (arm is null)
        {
            issues.Add(new("error", "NOT_ARMADS", "只支持 Keil5 ARMADS 工程，请确认选择了 ARM32 Target。"));
        }
        var controls = arm?.Element("Cads")?.Element("VariousControls");
        var assemblyControls = arm?.Element("Aads")?.Element("VariousControls");
        var miscellaneous = arm?.Element("ArmAdsMisc");
        foreach (var region in miscellaneous?.Element("OnChipMemories")?.Elements() ?? [])
        {
            if (region.Name.LocalName is "IROM" or "IRAM" && Text(region, "Size") is { Length: > 0 } size)
            {
                memory.Add(new(region.Name.LocalName, Number(Text(region, "StartAddress")), Number(size)));
            }
        }
        if (Text(miscellaneous, "BigEnd") == "1" || Text(miscellaneous, "nSecure") == "1")
        {
            issues.Add(new("error", "CPU_MODE", "启用了大端或安全域配置，本版不能映射为普通 STM32 GCC 固件。"));
        }
        if (Text(common, "UseEnv") == "1" || new[] { "IncludePath", "LibPath", "Asm", "Cmp", "Linker" }.Any(name => Text(common, name).Length > 0))
        {
            issues.Add(new("error", "ENVIRONMENT", "工程依赖 Keil 全局环境或附加工具目录，请先把依赖显式整理到工程 IncludePath 和源码中。"));
        }
        var includes = Split(Text(controls, "IncludePath"), ';');
        var defines = SplitDefines(Text(controls, "Define"));
        var sources = new List<string>();
        var supportFiles = new List<string>();
        var excluded = new List<string>();
        foreach (var group in target.Element("Groups")?.Elements("Group") ?? [])
        {
            var groupOptions = group.Element("GroupOption");
            var groupExcluded = Text(groupOptions?.Element("CommonProperty"), "IncludeInBuild") == "0";
            CheckOverrides(groupOptions, "组 " + Text(group, "GroupName"), issues);
            foreach (var file in group.Element("Files")?.Elements("File") ?? [])
            {
                var path = Text(file, "FilePath");
                if (path.Length == 0)
                {
                    issues.Add(new("error", "FILE_PATH", "Target 含空 FilePath，请先修正 Keil 工程。"));
                    continue;
                }
                var fileOptions = file.Element("FileOption");
                if (groupExcluded || Text(fileOptions?.Element("CommonProperty"), "IncludeInBuild") == "0")
                {
                    excluded.Add(path);
                    continue;
                }
                CheckOverrides(fileOptions, path, issues);
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is ".c" or ".cpp" or ".cc" or ".cxx" or ".s")
                {
                    sources.Add(path);
                }
                else if (extension is ".lib" or ".a" or ".o" or ".obj")
                {
                    issues.Add(new("error", "BINARY_LIBRARY", "无法确认 Keil 二进制库与 GCC ABI 兼容，请提供源码或 GCC 版本：" + path));
                }
                else if (extension is ".h" or ".hpp" or ".hh" or ".inc" or ".inl" or ".ipp" or ".tpp" or ".txt" or ".md" or ".sct" or ".ld")
                {
                    supportFiles.Add(path);
                }
                else
                {
                    issues.Add(new("error", "UNKNOWN_FILE", "无法识别构建输入，需手工迁移：" + path));
                }
            }
        }
        if (sources.Count == 0)
        {
            issues.Add(new("error", "NO_SOURCES", "所选 Target 没有可迁移的源码。"));
        }
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
        {
            issues.Add(new("error", "DUPLICATE_SOURCE", "Target 有重复源码路径，请先整理 Keil 工程。"));
        }
        if (Text(controls, "MiscControls").Length > 0 || Text(controls, "Undefine").Length > 0 ||
            new[] { "MiscControls", "Define", "Undefine", "IncludePath" }.Any(name => Text(assemblyControls, name).Length > 0) ||
            Text(arm?.Element("LDads"), "Misc").Length > 0)
        {
            issues.Add(new("error", "CUSTOM_FLAGS", "含自定义编译/汇编/链接参数或取消宏定义。本版不能可靠翻译，请先逐项迁移为 GCC 配置。"));
        }
        var linker = arm?.Element("LDads");
        if (Text(linker, "useTarget") == "0")
        {
            issues.Add(new("error", "LINKER_MEMORY", "链接器未使用 Target 内存布局，本版不能自动等价转换，请先核对 GNU 链接脚本。"));
        }
        if (Text(linker, "useFile") == "1" || Text(linker, "ScatterFile").Length > 0)
        {
            issues.Add(new("error", "SCATTER", "使用自定义 scatter 文件；请先提供等价 GNU 链接脚本。插件不猜测 bootloader、分区或 RAM 布局。"));
        }
        if (Text(common, "CreateLib") == "1")
        {
            issues.Add(new("error", "LIBRARY_TARGET", "静态库 Target 不能直接转成固件工程，请选择应用 Target。"));
        }
        if (Text(common, "useMicroLIB") == "1" || Text(arm?.Element("ArmAdsMisc"), "useMicroLIB") == "1")
        {
            issues.Add(new("warning", "MICROLIB", "Keil MicroLIB 改用 GCC/newlib；stdio 重定向、堆和 C++ 初始化需重新验证。"));
        }
        if (Text(miscellaneous, "useUlib") == "1")
        {
            issues.Add(new("warning", "MICROLIB", "Keil MicroLIB 改用 GCC/newlib；stdio 重定向、堆和 C++ 初始化需重新验证。"));
        }
        var rte = target.Document?.Root?.Element("RTE");
        if (rte?.Element("files")?.Elements().Any() == true ||
            rte?.Element("components")?.Elements().Any(component =>
                component.Attribute("Cclass")?.Value != "CMSIS" || component.Attribute("Cgroup")?.Value != "CORE") == true)
        {
            issues.Add(new("error", "RTE_COMPONENT", "含自动管理的 RTE 文件或中间件组件。请先将组件源码、头文件和配置显式加入所选 Target；插件不下载或猜测 Keil Pack 依赖。"));
        }
        if ((common?.Descendants() ?? []).Any(element => element.Name.LocalName.StartsWith("RunUserProg", StringComparison.Ordinal) && element.Value.Trim() == "1"))
        {
            issues.Add(new("error", "BUILD_EVENT", "Target 启用了构建事件。插件不执行或自动迁移脚本；请先检查其生成文件和必要步骤。"));
        }
        issues.Add(new("warning", "COMPILER_CHANGE", "保留源码、IncludePath、宏和排除项；优化、告警、调试器、下载器及 Keil 运行库配置不自动等价迁移。生成成功不代表编译或实板通过。"));
        return new(Text(target, "TargetName"), Text(common, "Device"), Text(target, "pCCUsed"),
            sources.ToArray(), includes, defines, excluded.ToArray(), issues.ToArray(), memory.ToArray(), supportFiles.ToArray());
    }

    private static void CheckOverrides(XElement? options, string label, List<ImportIssue> issues)
    {
        if (options is null)
        {
            return;
        }
        // IncludeInBuild 可安全解释；编译器专用的逐文件/逐组覆盖必须先人工确认。
        if (options.Elements().Any(element => element.Name.LocalName != "CommonProperty") ||
            options.Element("CommonProperty")?.Elements().Any(element => element.Name.LocalName != "IncludeInBuild" &&
                element.Name.LocalName != "StopOnExitCode" && element.Value.Trim() is not ("" or "0" or "2")) == true)
        {
            issues.Add(new("error", "LOCAL_OPTIONS", "含逐文件或逐组编译设置，本版不静默丢弃：" + label));
        }
    }

    public static string Text(XElement? element, string name) => element?.Element(name)?.Value.Trim() ?? "";

    private static string[] Split(string value, char separator) => value.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static uint Number(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt32(value[2..], 16) : uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static string[] SplitDefines(string value)
    {
        var defines = value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var define in defines)
        {
            if (!MacroPattern().IsMatch(define))
            {
                throw new InvalidOperationException("宏定义包含复杂表达式、引号或未知写法，请手工迁移：" + define);
            }
            _ = ImportPaths.CMakeValue(define);
        }
        return defines.Distinct(StringComparer.Ordinal).ToArray();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(=[A-Za-z0-9_+./() -]+)?$")]
    private static partial Regex MacroPattern();
}
