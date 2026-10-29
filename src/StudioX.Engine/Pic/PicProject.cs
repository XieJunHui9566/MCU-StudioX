namespace StudioX.Engine.Pic;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>校验 PIC 器件和工具身份；第一阶段不开放硬件入口或通用 GCC 参数。</summary>
public static class PicProject
{
    public static async Task ValidateAsync(string root, ProjectManifest project, CancellationToken token)
    {
        if (project.Kind != ProjectKind.Pic || project.Pic is null || project.Arduino is not null || project.CubeMx is not null ||
            project.Espressif is not null || project.MicroPython is not null || project.Zephyr is not null || project.Logic is not null || project.PinMapping is not null)
        {
            throw new StudioXException("PROJECT_PIC", "PIC 工程身份或模式配置无效。");
        }
        PackValidator.Token(project.Name);
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        var device = pack.Devices.SingleOrDefault(item => item.Id == project.DeviceId);
        if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion || device is null ||
            project.Pic != device.Pic || project.ToolsetId != device.ToolsetId || project.ToolsetVersion != device.ToolsetVersion ||
            project.CompilerId != device.CompilerId || !device.Templates.Any(item => item.Id == project.TemplateId))
        {
            throw new StudioXException("PROJECT_PIC", "PIC 工程与器件包的型号、模板或工具版本不一致。");
        }
        PackValidator.Validate(pack, PathBoundary.Resolve(root, "device"));
        if (File.Exists(PathBoundary.Resolve(root, ProjectBuildSettings.RelativePath)))
        {
            throw new StudioXException("PIC_BUILD_SETTINGS", "PIC 首阶段使用固定 C99 / O0 编译流程；不支持 .studiox/build.json 中的 GCC/CMake 参数。");
        }
    }

    internal static void ValidateTools(ResolvedToolset tools)
    {
        if (tools.Manifest.Purpose != "pic-xc8" || tools.Manifest.ComponentVersions?.GetValueOrDefault("xc8") != "4.00" ||
            tools.Manifest.ComponentVersions?.GetValueOrDefault("PIC16Fxxx_DFP") != "1.9.176")
        {
            throw new StudioXException("PIC_TOOLS", "需要锁定的 XC8 4.00 与 PIC16Fxxx DFP 1.9.176 独立组件。");
        }
        _ = tools.ResourceDirectory("pic-dfp");
        _ = tools.ResourceDirectory("pic-headers");
    }
}
