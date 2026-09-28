namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

public sealed partial class ProjectService
{
    private static readonly SemaphoreSlim pinMappingGate = new(1, 1);

    /// <summary>为已有 AGM 工程显式启用基础映射，保留原 .ve、MCU 源码和全部器件资源。</summary>
    public static async Task<ProjectManifest> EnableAg32PinMappingAsync(string directory, CancellationToken cancellationToken = default)
    {
        await pinMappingGate.WaitAsync(cancellationToken);
        try
        {
            var root = Path.GetFullPath(directory);
            var project = await ReadAsync(root, cancellationToken);
            if (project.Kind != ProjectKind.Pack || Ag32DeviceCatalog.Find(project.DeviceId) is not { CanMap: true } profile)
            {
                throw new StudioXException("PROJECT_PIN_MAPPING_DEVICE", "基础引脚映射需要已核实型号、封装和逻辑保留区的 AGM 器件。");
            }
            if (project.Logic is not null)
            {
                // 自定义顶层必须由综合流程编译，不能以默认网表替代用户的 Verilog。
                throw new StudioXException("PROJECT_PIN_MAPPING_CUSTOM_LOGIC", "该工程已启用自定义 Verilog，不能切换为默认 MCU 引脚映射流程。");
            }
            var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), cancellationToken);
            PackValidator.Validate(pack, Path.Combine(root, "device"));
            var device = pack.Devices.SingleOrDefault(item => string.Equals(item.Id, project.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (pack.Id != project.PackId || pack.Version != project.PackVersion ||
                !string.Equals(pack.Vendor, "AGM", StringComparison.OrdinalIgnoreCase) || device is null ||
                device.ToolsetId != project.ToolsetId || device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId ||
                !profile.Matches(device))
            {
                throw new StudioXException("PROJECT_PIN_MAPPING_DEVICE", "工程锁定的 AGM 器件信息与 device/manifest.json 不一致，不能启用引脚映射。");
            }
            var settings = project.PinMapping ?? new Ag32PinMappingProjectSettings(profile.TargetDevice);
            var scaffold = CreateAg32PinMappingScaffold(profile);
            var pinMapPath = PathBoundary.Resolve(root, settings.PinMapFile);
            if (Directory.Exists(pinMapPath))
            {
                throw new StudioXException("PROJECT_PIN_MAPPING_FILE", "引脚映射路径被目录占用，请修复 logic/pins.ve。");
            }
            if (project.PinMapping is not null && File.Exists(pinMapPath))
            {
                return project;
            }
            var createdPinMap = false;
            var temporaryPinMap = pinMapPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                if (!File.Exists(pinMapPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pinMapPath)!);
                    // 先完成骨架再发布，取消不能留下半个 .ve；无覆盖移动保护新出现的用户文件。
                    await File.WriteAllTextAsync(temporaryPinMap, scaffold, cancellationToken);
                    File.Move(temporaryPinMap, pinMapPath, overwrite: false);
                    createdPinMap = true;
                }
                var enabled = project with
                {
                    PinMapping = settings
                };
                await JsonStore.WriteAsync(PathBoundary.Resolve(root, ".studiox/project.json"), enabled, cancellationToken);
                return enabled;
            }
            catch
            {
                // 失败不保留本次新建的半成品；只有内容仍为我们的骨架时才移除。
                if (createdPinMap && File.Exists(pinMapPath) && await File.ReadAllTextAsync(pinMapPath, CancellationToken.None) == scaffold)
                {
                    File.Delete(pinMapPath);
                }
                throw;
            }
            finally
            {
                if (File.Exists(temporaryPinMap))
                {
                    File.Delete(temporaryPinMap);
                }
            }
        }
        finally { pinMappingGate.Release(); }
    }

    private static void ValidateAg32PinMappingSettings(string directory, ProjectManifest project)
    {
        if (project.PinMapping is not { } settings)
        {
            // 老工程没有此字段时保持原行为，必须显式启用，避免自动生成并烧录映射。
            return;
        }
        if (project.Kind != ProjectKind.Pack || Ag32DeviceCatalog.Find(project.DeviceId) is not { CanMap: true } profile ||
            settings != new Ag32PinMappingProjectSettings(profile.TargetDevice))
        {
            throw new StudioXException("PROJECT_PIN_MAPPING_SETTINGS", "AG32 基础引脚映射配置无效或不受当前版本支持。");
        }
        _ = PathBoundary.Resolve(directory, settings.PinMapFile);
    }

    private static string CreateAg32PinMappingScaffold(Ag32DeviceProfile profile) => $"""
        # {profile.DeviceId} / {profile.TargetDevice} MCU 引脚映射
        # C 代码使用内部 GPIO，下面按真实 {profile.PackageName} 板级连线绑定到封装引脚。
        # 示例仅说明语法，注释中的引脚不构成该开发板默认连线：
        # GPIO4_4 PIN_21
        # GPIO4_4 PIN_2
        # 两个示例只能按实际接线选择其一；电源、地、配置等固定脚不可占用。
        # 未填写映射时不应下载：先核对芯片引脚表、板级原理图和外设复用。
        # HSECLK / SYSCLK / BUSCLK 沿用厂商 VE 转换器规则；如需填写，须以
        # 实际晶振与官方支持组合为依据。StudioX 不猜测或改写时钟频率。
        # 基础映射由内置 AGM VE / Supra 工具生成逻辑 BIN，不需要 Quartus。
        # 每次修改后重新构建和下载映射；只下载 MCU 固件不会更新映射。
        """ + "\n";

    private static async Task WriteAg32PinMappingScaffoldAsync(string staging, string deviceId, Ag32PinMappingProjectSettings settings, CancellationToken token)
    {
        var profile = Ag32DeviceCatalog.Require(deviceId);
        var pinMap = PathBoundary.Resolve(staging, settings.PinMapFile);
        Directory.CreateDirectory(Path.GetDirectoryName(pinMap)!);
        await File.WriteAllTextAsync(pinMap, CreateAg32PinMappingScaffold(profile), token);
        await File.WriteAllTextAsync(Path.Combine(staging, "logic", "README.md"),
            "# AG32 基础引脚映射\n\n" +
            $"`pins.ve` 将 MCU 内部 GPIO、外设功能映射到 {profile.DeviceId} 的真实 {profile.PackageName} 封装引脚。模板不预设开发板连线，请按原理图填写；例如 `GPIO4_4 PIN_21` 只是语法示例。\n\n" +
            "基础模式由内置 AGM VE / Supra 工具构建独立映射镜像，不需要 Quartus 或自定义 Verilog。修改 `.ve` 后必须重新构建并下载映射镜像，仅编译、下载 C 固件不会改变封装引脚连接。下载映射保留 MCU 应用区；先核对原理图、器件和当前芯片程序的内部 GPIO。\n\n" +
            "HSECLK、SYSCLK、BUSCLK 是原始厂商配置。按实际晶振和厂商允许的组合填写，IDE 不猜测频率，不在编辑时修改配置。\n\n" +
            "若创建时另选自定义 Verilog，仍生成 `.ve`；顶部编译使用内置原生综合与 Supra 布局布线联动生成自定义逻辑镜像。AG32 页面提供构建配置、SDC 和 RTL 波形仿真入口。\n", token);
    }
}
