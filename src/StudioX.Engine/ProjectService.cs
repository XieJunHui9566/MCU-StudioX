namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

public sealed class ProjectService(Func<string, CancellationToken, Task>? initializeRepository = null)
{
    public static BuildPlan Plan(InstalledPack pack, string deviceId, string templateId, string name, bool enableAg32Logic = false)
    {
        PackValidator.Token(name);
        var device = pack.Manifest.Devices.SingleOrDefault(d => d.Id == deviceId)
            ?? throw new StudioXException("PROJECT_DEVICE", "请选择明确的芯片型号。");
        if (!device.Templates.Any(t => t.Id == templateId)) throw new StudioXException("PROJECT_TEMPLATE", "请选择明确的模板。");
        if (enableAg32Logic && !IsAg32LogicDevice(pack, deviceId))
            throw new StudioXException("PROJECT_LOGIC_DEVICE", "逻辑/Verilog 特殊模式目前仅支持 AGM AG32VF303CCT6（LQFP48）。");
        var logic = enableAg32Logic ? new Ag32LogicProjectSettings("AGRV2KL48", "logic/user_logic.v", "logic/pins.ve") : null;
        return new BuildPlan(new ProjectManifest(1, name, pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash,
            deviceId, templateId, device.ToolsetId, device.ToolsetVersion, device.CompilerId, Logic: logic), TemplateResolver.Resolve(device, templateId));
    }

    public static bool IsAg32LogicDevice(InstalledPack pack, string deviceId) =>
        string.Equals(pack.Manifest.Vendor, "AGM", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(deviceId, "AG32VF303CCT6", StringComparison.OrdinalIgnoreCase);

    public async Task<ProjectManifest> CreateAsync(InstalledPack pack, string deviceId, string templateId, string name,
        string destination, CancellationToken cancellationToken = default, bool enableAg32Logic = false)
    {
        _ = Plan(pack, deviceId, templateId, name, enableAg32Logic);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target)) throw new StudioXException("PROJECT_EXISTS", "目标路径已存在，请选择新的工程目录。");
        // 选择器只读包目录；复制任何模板或 SDK 前，对这个包进行完整校验并使用新读取的清单。
        pack = await PackRepository.VerifyAsync(pack, cancellationToken);
        var plan = Plan(pack, deviceId, templateId, name, enableAg32Logic);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".studiox-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var source in Directory.EnumerateFiles(pack.RootDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(pack.RootDirectory, source).Replace('\\', '/');
                // 新模板只拷贝实际选用的 SDK；链接脚本是必要构建输入，即使位于 sdk/ 也不能裁掉。
                if (plan.Device.Templates.Single(t => t.Id == templateId).Build is not null && relative.StartsWith("sdk/", StringComparison.Ordinal) &&
                    relative != plan.Device.LinkerScript &&
                    !plan.Device.Sources.Contains(relative, StringComparer.Ordinal) &&
                    !plan.Device.IncludeDirectories.Any(include => relative.StartsWith(include.TrimEnd('/') + "/", StringComparison.Ordinal))) continue;
                var copy = PathBoundary.Resolve(Path.Combine(staging, "device"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(PathBoundary.Resolve(pack.RootDirectory, relative), copy);
            }
            Directory.CreateDirectory(Path.Combine(staging, "src"));
            var template = plan.Device.Templates.Single(t => t.Id == templateId);
            File.Copy(PathBoundary.Resolve(pack.RootDirectory, template.EntryFile), Path.Combine(staging, "src", "main.c"));
            Directory.CreateDirectory(Path.Combine(staging, "include"));
            if (template.Files is not null)
                foreach (var (relative, source) in template.Files)
                {
                    if (!(relative.StartsWith("src/", StringComparison.Ordinal) || relative.StartsWith("include/", StringComparison.Ordinal)))
                        throw new StudioXException("PROJECT_TEMPLATE_FILE", "模板文件超出用户源码目录。");
                    var file = PathBoundary.Resolve(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.Copy(PathBoundary.Resolve(pack.RootDirectory, source), file);
                }
            if (plan.Project.Logic is { } logic)
                await WriteAg32LogicScaffoldAsync(staging, logic, cancellationToken);
            await JsonStore.WriteAsync(Path.Combine(staging, ".studiox", "project.json"), plan.Project, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, "CMakeLists.txt"), CMakeGenerator.Render(plan), cancellationToken);
            foreach (var (relative, content) in new[] { (CMakeGenerator.DeviceListPath, CMakeGenerator.RenderDevice(plan)), (CMakeGenerator.PlatformPath, CMakeGenerator.RenderPlatform(plan)) })
            {
                var path = PathBoundary.Resolve(staging, relative);
                // 器件包不能占用生成器的固定入口，避免静默覆盖包内文件。
                if (File.Exists(path)) throw new StudioXException("PROJECT_RESERVED_FILE", $"器件包占用了工程生成器路径：{relative}");
                await File.WriteAllTextAsync(path, content, cancellationToken);
            }
            var gitignore = ".build/\nbuild/\ncmake-build-*/\n.studiox/debug.json\n.studiox/breakpoints.json\n*.user\n";
            if (plan.Project.Logic is not null)
                gitignore += "logic/db/\nlogic/incremental_db/\nlogic/output_files/\nlogic/*.vo\nlogic/*.bin\n";
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"), gitignore, cancellationToken);
            if (initializeRepository is not null) await initializeRepository(staging, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return plan.Project;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    public static async Task<ProjectManifest> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var project = await JsonStore.ReadAsync<ProjectManifest>(Path.Combine(directory, ".studiox", "project.json"), cancellationToken);
        if (project.FormatVersion != 1) throw new StudioXException("PROJECT_FORMAT", "不支持的工程格式。");
        if (project.Kind == ProjectKind.CubeMx) CubeMxImportService.Validate(directory, project);
        else if (project.Kind != ProjectKind.Pack || project.CubeMx is not null) throw new StudioXException("PROJECT_KIND", "不支持的工程类型。");
        else PackValidator.Token(project.Name);
        if (project.Logic is { } logic && (project.Kind != ProjectKind.Pack ||
            !string.Equals(project.DeviceId, "AG32VF303CCT6", StringComparison.OrdinalIgnoreCase) ||
            logic != new Ag32LogicProjectSettings("AGRV2KL48", "logic/user_logic.v", "logic/pins.ve")))
            throw new StudioXException("PROJECT_LOGIC_SETTINGS", "AG32 逻辑工程配置无效或不受当前版本支持。");
        PackValidator.Token(project.ToolsetId); PackValidator.Version(project.ToolsetVersion);
        return project;
    }

    private static async Task WriteAg32LogicScaffoldAsync(string staging, Ag32LogicProjectSettings logic, CancellationToken token)
    {
        var directory = Path.Combine(staging, "logic");
        Directory.CreateDirectory(directory);
        // 不假设用户开发板的布线：错误的物理引脚映射可能与 MCU 外设复用冲突。
        await File.WriteAllTextAsync(PathBoundary.Resolve(staging, logic.VerilogFile),
            "// 编辑起点，不能直接作为可下载设计。先填写 .ve，运行厂商 Prepare LOGIC，\n" +
            "// 核对所生成的顶层与接口，再按需加入端口、逻辑并由顶层实例化。\n" +
            "module user_logic;\nendmodule\n", token);
        await File.WriteAllTextAsync(PathBoundary.Resolve(staging, logic.PinMapFile),
            "# AG32VF303CCT6 / AGRV2KL48 逻辑与物理引脚映射\n" +
            "# 请核对所用 LQFP48 开发板原理图和 AGM 引脚表，再添加 MCU/逻辑信号映射。\n" +
            "# 此处故意不预设 PIN_N：厂商示例常针对其他封装，直接沿用可能造成引脚冲突。\n", token);
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"),
            "# AG32 逻辑/Verilog 特殊模式\n\n" +
            "此目录独立于 `src/main.c` 的 MCU 固件。`user_logic.v` 只是编辑起点，不能直接综合下载；`pins.ve` 用于定义逻辑信号、MCU 功能与物理引脚的映射。当前目标为 AG32VF303CCT6 的 AGRV2KL48（LQFP48）。\n\n" +
            "StudioX 当前没有 `Prepare LOGIC` 任务。先完成 `pins.ve` 的真实板级映射，再把此目录的文件复制或链接到 **AGM AgRV SDK / PlatformIO 配套工程**。在该配套工程的 `platformio.ini` 中配置相对路径，例如：\n\n" +
            "```ini\n[setup_logic]\nlogic_ve = logic/pins.ve\nlogic_device = AGRV2KL48\nip_name = user_logic\nlogic_dir = logic\n```\n\n" +
            "旧版 AgRV SDK 的字段名可能不同，请以实际安装版本的 `platformio.ini` 模板为准。在配套工程运行厂商的 `Prepare LOGIC`，检查它从 `.ve` 生成的顶层、接口和工程，然后按生成接口修改 `user_logic.v` 并接入顶层。上述配置只对 AGM/PlatformIO 配套工程生效；仅在 StudioX 工程中编辑文件不会执行生成步骤。\n\n" +
            "逻辑综合和转换需要 **Quartus II Full** 与 **Supra**（Supra 通常随 AgRV SDK 提供）：用 Quartus II 编译为 `.vo`，再用 Supra 转换为逻辑 `.bin`（默认设计名 `pins` 时为 `logic/pins.bin`）。逻辑镜像需要通过厂商工具和适配的烧录器单独下载；MCU 固件构建与下载不会自动包含逻辑。\n\n" +
            "配置 `pins.ve` 前请检查板级原理图、封装引脚和 MCU 外设复用。这里未预设任何物理引脚。\n", token);
    }
}
