namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

public sealed partial class ProjectService(Func<string, CancellationToken, Task>? initializeRepository = null)
{
    public static BuildPlan Plan(InstalledPack pack, string deviceId, string templateId, string name, bool enableAg32Logic = false)
    {
        PackValidator.Token(name);
        var device = pack.Manifest.Devices.SingleOrDefault(d => d.Id == deviceId)
            ?? throw new StudioXException("PROJECT_DEVICE", "请选择明确的芯片型号。");
        if (!device.Templates.Any(t => t.Id == templateId))
        {
            throw new StudioXException("PROJECT_TEMPLATE", "请选择明确的模板。");
        }
        var selectedTemplate = device.Templates.Single(template => template.Id == templateId);
        var components = DevelopmentComponentRequirements.ForTemplate(device, selectedTemplate);
        // 特殊模式只由创建选项开启，模板资源不能替用户勾选或锁定模式。
        if (enableAg32Logic && !IsAg32LogicDevice(pack, deviceId))
        {
            throw new StudioXException("PROJECT_LOGIC_DEVICE", "逻辑/Verilog 特殊模式需要已核实型号、封装和逻辑保留区的 AGM 器件包。");
        }
        var ag32 = IsAg32LogicDevice(pack, deviceId) ? Ag32DeviceCatalog.Require(deviceId) : null;
        var logicComponent = components.SingleOrDefault(c => c.Id == "agm.logic");
        var mappingComponent = components.SingleOrDefault(c => c.Id == "agm.pin-mapping");
        var logic = enableAg32Logic ? new Ag32LogicProjectSettings(ag32!.TargetDevice, "logic/user_logic.v", "logic/pins.ve",
            ToolsetVersion: logicComponent?.Version ?? "1.0.0", CompilerId: logicComponent?.CompilerId ?? "agm.native") : null;
        var pinMapping = ag32 is not null ? new Ag32PinMappingProjectSettings(ag32.TargetDevice,
            ToolsetVersion: mappingComponent?.Version ?? "1.0.0", CompilerId: mappingComponent?.CompilerId ?? "agm.ve") : null;
        var espressif = device.Espressif is { } profile
            ? new EspressifProjectSettings(profile.Framework, profile.Target, profile.SdkVersion) : null;
        if (selectedTemplate.MicroPython is { } microPython)
        {
            microPython.Validate(deviceId);
            if (enableAg32Logic)
            {
                throw new StudioXException("PROJECT_MICROPYTHON", "MicroPython 工程不使用 AG32 逻辑模式。");
            }
            return new(new ProjectManifest(1, name, pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash,
                deviceId, templateId, "", "", "", ProjectKind.MicroPython, EntryFile: "main.py", MicroPython: microPython,
                DevelopmentComponents: components), device);
        }
        var entryFile = selectedTemplate.EspressifExample is { } example
            ? selectedTemplate.EntryFile[(example.ExampleDirectory.TrimEnd('/').Length + 1)..] : null;
        return new BuildPlan(new ProjectManifest(1, name, pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash,
            deviceId, templateId, device.ToolsetId, device.ToolsetVersion, device.CompilerId, Logic: logic, Espressif: espressif, EntryFile: entryFile,
            PinMapping: pinMapping, DevelopmentComponents: components), TemplateResolver.Resolve(device, templateId, enableAg32Logic));
    }

    public static bool IsAg32LogicDevice(InstalledPack pack, string deviceId)
    {
        var device = pack.Manifest.Devices.SingleOrDefault(item => string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        return string.Equals(pack.Manifest.Vendor, "AGM", StringComparison.OrdinalIgnoreCase) &&
            device is not null && Ag32DeviceCatalog.Find(deviceId) is { CanMap: true } profile && profile.Matches(device);
    }

    public async Task<ProjectManifest> CreateAsync(InstalledPack pack, string deviceId, string templateId, string name,
        string destination, CancellationToken cancellationToken = default, bool enableAg32Logic = false)
    {
        _ = Plan(pack, deviceId, templateId, name, enableAg32Logic);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new StudioXException("PROJECT_EXISTS", "目标路径已存在，请选择新的工程目录。");
        }
        // 选择器只读包目录；复制任何模板或 SDK 前，对这个包进行完整校验并使用新读取的清单。
        pack = await PackRepository.VerifyAsync(pack, cancellationToken);
        var plan = Plan(pack, deviceId, templateId, name, enableAg32Logic);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".studiox-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            if (plan.Project.Kind == ProjectKind.MicroPython)
            {
                await MicroPythonProject.WriteAsync(pack, plan, staging, cancellationToken);
                if (initializeRepository is not null)
                {
                    await initializeRepository(staging, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await PublishDirectoryAsync(staging, target, cancellationToken);
                return plan.Project;
            }
            foreach (var source in Directory.EnumerateFiles(pack.RootDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(pack.RootDirectory, source).Replace('\\', '/');
                // 新模板只拷贝实际选用的 SDK；链接脚本是必要构建输入，即使位于 sdk/ 也不能裁掉。
                if (plan.Device.Templates.Single(t => t.Id == templateId).Build is not null && relative.StartsWith("sdk/", StringComparison.Ordinal) &&
                    relative != plan.Device.LinkerScript &&
                    !plan.Device.Sources.Contains(relative, StringComparer.Ordinal) &&
                    !plan.Device.IncludeDirectories.Any(include => relative.StartsWith(include.TrimEnd('/') + "/", StringComparison.Ordinal)))
                {
                    continue;
                }
                var copy = PathBoundary.Resolve(Path.Combine(staging, "device"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(PathBoundary.Resolve(pack.RootDirectory, relative), copy);
            }
            var template = plan.Device.Templates.Single(t => t.Id == templateId);
            if (template.EspressifExample is null)
            {
                Directory.CreateDirectory(Path.Combine(staging, "src"));
                var entry = plan.Project.Logic is not null ? template.Ag32Sources?.EntryFile ?? template.EntryFile : template.EntryFile;
                File.Copy(PathBoundary.Resolve(pack.RootDirectory, entry), Path.Combine(staging, "src", "main.c"));
            }
            Directory.CreateDirectory(Path.Combine(staging, "include"));
            if (template.Files is not null)
            {
                foreach (var (relative, source) in template.Files)
                {
                    if (!(relative.StartsWith("src/", StringComparison.Ordinal) || relative.StartsWith("include/", StringComparison.Ordinal)))
                    {
                        throw new StudioXException("PROJECT_TEMPLATE_FILE", "模板文件超出用户源码目录。");
                    }
                    var file = PathBoundary.Resolve(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.Copy(PathBoundary.Resolve(pack.RootDirectory, source), file);
                }
            }
            if (plan.Project.PinMapping is { } pinMapping)
            {
                await WriteAg32PinMappingScaffoldAsync(staging, plan.Project.DeviceId, pinMapping, cancellationToken);
            }
            if (plan.Project.Logic is { } logic)
            {
                await WriteAg32LogicScaffoldAsync(staging, plan.Project.DeviceId, logic, cancellationToken);
                if (template.Ag32Sources is { } ag32Sources)
                {
                    File.Copy(PathBoundary.Resolve(pack.RootDirectory, ag32Sources.PinMapFile), PathBoundary.Resolve(staging, logic.PinMapFile), overwrite: true);
                    File.Copy(PathBoundary.Resolve(pack.RootDirectory, ag32Sources.VerilogFile), PathBoundary.Resolve(staging, logic.VerilogFile), overwrite: true);
                    await File.AppendAllTextAsync(Path.Combine(staging, "logic", "README.md"),
                        "\n此模板已提供可综合的内部 GPIO4_0 → FPGA → GPIO4_1 回环，不占用封装脚。可在此基础上编辑 user_logic.v 和 pins.ve；FreeRTOS 运行在 MCU，逻辑电路独立运行。\n", cancellationToken);
                }
            }
            await JsonStore.WriteAsync(Path.Combine(staging, ".studiox", "project.json"), plan.Project, cancellationToken);
            if (plan.Project.PinMapping is { } systemMapping)
            {
                var ve = await File.ReadAllBytesAsync(PathBoundary.Resolve(staging, systemMapping.PinMapFile), cancellationToken);
                await Ag32SystemSupport.WriteAsync(staging, Ag32SystemSupport.Render(ve, null, []), systemMapping.PinMapFile, ve, null, cancellationToken);
                // 只创建新工程入口；已有工程保存配置时不触碰用户 main.c。
                if (plan.Project.TemplateId == "minimal")
                {
                    await File.WriteAllTextAsync(Path.Combine(staging, "src", "main.c"), """
                    #include "StudioX_System.h"

                    volatile uint32_t app_heartbeat = 0;

                    int main(void)
                    {
                        for (;;)
                        {
                            ++app_heartbeat;
                            Delay_1ms();
                        }
                    }
                    """ + "\n", cancellationToken);
                }
            }
            if (plan.Project.Espressif is not null)
            {
                await EspressifProjectScaffold.WriteAsync(staging, plan, cancellationToken);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(staging, "CMakeLists.txt"), CMakeGenerator.Render(plan), cancellationToken);
                foreach (var (relative, content) in new[] { (CMakeGenerator.DeviceListPath, CMakeGenerator.RenderDevice(plan)), (CMakeGenerator.PlatformPath, CMakeGenerator.RenderPlatform(plan)) })
                {
                    var path = PathBoundary.Resolve(staging, relative);
                    // 器件包不能占用生成器的固定入口，避免静默覆盖包内文件。
                    if (File.Exists(path))
                    {
                        throw new StudioXException("PROJECT_RESERVED_FILE", $"器件包占用了工程生成器路径：{relative}");
                    }
                    await File.WriteAllTextAsync(path, content, cancellationToken);
                }
            }
            var gitignore = ".build/\nbuild/\ncmake-build-*/\n.studiox/debug.json\n.studiox/breakpoints.json\n*.user\n";
            if (plan.Project.PinMapping is not null || plan.Project.Logic is not null)
            {
                gitignore += "logic/db/\nlogic/incremental_db/\nlogic/output_files/\nlogic/*.vo\nlogic/*.bin\n";
            }
            if (plan.Project.Espressif is not null)
            {
                gitignore += "sdkconfig.old\nmanaged_components/\n";
            }
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"), gitignore, cancellationToken);
            if (initializeRepository is not null)
            {
                await initializeRepository(staging, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await PublishDirectoryAsync(staging, target, cancellationToken);
            return plan.Project;
        }
        finally { if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); } }
    }

    private static async Task PublishDirectoryAsync(string staging, string target, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(staging, target);
                return;
            }
            // Windows 文件扫描器可能短暂持有刚复制的 SDK 目录；不改 ACL，不覆盖并发创建的目标。
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 5 or 32 or 33
                && attempt < 8 && Directory.Exists(staging) && !Directory.Exists(target) && !File.Exists(target))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), token);
            }
        }
    }

    public static async Task<ProjectManifest> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var project = await JsonStore.ReadAsync<ProjectManifest>(Path.Combine(directory, ".studiox", "project.json"), cancellationToken);
        if (project.FormatVersion != 1)
        {
            throw new StudioXException("PROJECT_FORMAT", "不支持的工程格式。");
        }
        ProjectDevelopmentComponents.ValidateSnapshot(project);
        if (project.Kind == ProjectKind.MicroPython)
        {
            await MicroPythonProject.ValidateAsync(directory, project, cancellationToken);
            return project;
        }
        if (project.MicroPython is not null)
        {
            throw new StudioXException("PROJECT_MICROPYTHON", "MicroPython 配置与工程类型不一致。");
        }
        if (project.Kind == ProjectKind.CubeMx)
        {
            CubeMxImportService.Validate(directory, project);
        }
        else if (project.Kind == ProjectKind.Zephyr)
        {
            await ZephyrProjectSettings.ValidateAsync(directory, project, cancellationToken);
            return project;
        }
        else if (project.Kind != ProjectKind.Pack || project.CubeMx is not null)
        {
            throw new StudioXException("PROJECT_KIND", "不支持的工程类型。");
        }
        else
        {
            PackValidator.Token(project.Name);
        }
        if (project.Logic is { } logic && (project.Kind != ProjectKind.Pack ||
            Ag32DeviceCatalog.Find(project.DeviceId) is not { CanMap: true } profile ||
            logic.TargetDevice != profile.TargetDevice || logic.VerilogFile != "logic/user_logic.v" || logic.PinMapFile != "logic/pins.ve" ||
            logic.ToolsetId != "agm.logic" || logic.CompilerId != "agm.native"))
        {
            throw new StudioXException("PROJECT_LOGIC_SETTINGS", "AG32 逻辑工程配置无效或不受当前版本支持。");
        }
        if (project.Logic is { } validatedLogic)
        {
            PackValidator.Version(validatedLogic.ToolsetVersion);
        }
        ValidateAg32PinMappingSettings(directory, project);
        PackValidator.Token(project.ToolsetId);
        PackValidator.Version(project.ToolsetVersion);
        EspressifProjectScaffold.Validate(project);
        if (project.EntryFile is { } entryFile)
        {
            // 入口仅用于首次打开；用户可以重构源码，文件不存在时仍允许打开工程修复。
            _ = PathBoundary.Resolve(directory, entryFile);
        }
        return project;
    }

    private static async Task WriteAg32LogicScaffoldAsync(string staging, string deviceId, Ag32LogicProjectSettings logic, CancellationToken token)
    {
        var profile = Ag32DeviceCatalog.Require(deviceId);
        var directory = Path.Combine(staging, "logic");
        Directory.CreateDirectory(directory);
        // 不假设用户开发板的布线：错误的物理引脚映射可能与 MCU 外设复用冲突。
        await File.WriteAllTextAsync(PathBoundary.Resolve(staging, logic.VerilogFile),
            "// 编辑起点。先填写 .ve，按映射信号定义端口与逻辑。\n" +
            "// 顶部编译会生成顶层接口并联合构建 MCU 与 FPGA。\n" +
            "module user_logic;\nendmodule\n", token);
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"),
            "# AG32 逻辑/Verilog 特殊模式\n\n" +
            $"此目录独立于 `src/main.c` 的 MCU 固件。`user_logic.v` 只是编辑起点，不能直接综合下载；`pins.ve` 用于定义逻辑信号、MCU 功能与物理引脚的映射。当前目标为 {profile.DeviceId} 的 {profile.TargetDevice}（{profile.PackageName}）。\n\n" +
            "在 AG32 页面配置源文件、包含目录、宏和附加 SDC。顶部编译 / F7 联合运行 MCU GCC、VE 转换、内置原生 mapper 和 Supra 布局布线，生成两段独立镜像。无需外部 Quartus；Supra 许可须导入本机用户目录。\n\n" +
            "构建设置在 `.studiox/ag32-logic-build.json`，源文件路径相对工程。顶层 pins.v 和接口模板生成在 `.build/ag32-logic/<运行编号>/`；按模板核对 user_logic 的接口，不手工维护生成的顶层。失败保留原始日志，成功才建立可下载凭据。\n\n" +
            "顶部下载先构建，在日志中展示 MCU 与逻辑两段镜像的地址和 SHA-256，再分别写入校验、复位运行。当前实板身份检查仅开放已验证的 CCT6。\n\n" +
            "RTL 仿真在 AG32 页面配置 testbench；内置 Icarus 输出可缩放、筛选的 VCD 数字波形，支持 #delay、断言和 X/Z。仿真设置在 `.studiox/hdl-simulation.json`。新建模板需要连接 DUT 并添加激励和断言；当前不包含 SDF 布局后延时仿真。\n\n" +
            "配置 `pins.ve` 前请检查板级原理图、封装引脚和 MCU 外设复用。这里未预设任何物理引脚。\n", token);
    }
}
