namespace StudioX.Engine;

using StudioX.Foundation;

public sealed partial class Ag32PinMappingBuildService
{
    internal async Task ValidateBuildInputsAsync(string root, ProjectManifest project, CancellationToken token)
    {
        // 缺许可时不必先校验整套 GCC；引脚预检只查询厂商目录，不生成固件或建立构建凭据。
        await ValidateLicenseAsync(token);
        if (project.Logic is not null)
        {
            // 自定义逻辑允许 ASSIGN 等语法，不能套用基础 MCU 的引脚映射规则。
            return;
        }
        var settings = RequireSettings(project);
        var profile = await RequireDeviceIdentityAsync(root, project, token);
        using var lease = ToolUsageLease.Acquire(PathBoundary.Resolve(catalog.RootDirectory, settings.ToolsetId + "/" + settings.ToolsetVersion));
        var tools = await catalog.ResolveAsync(settings.ToolsetId, settings.ToolsetVersion, settings.CompilerId, token);
        var pins = await Ag32PinPlanCatalog.ReadAsync(tools, settings.TargetDevice, profile.PinCount, root, token);
        var assignablePins = pins.Pins.Where(pin => pin.CanAssign).Select(pin => pin.Number).ToHashSet();
        var source = await Ag32PinMappingValidation.ReadSourceAsync(root, settings, assignablePins, profile, token);
        Ag32PeripheralSupport.Validate(project.DeviceId, source, new Ag32PinPlanDocument(source).Assignments);
    }
}
