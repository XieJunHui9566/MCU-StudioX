namespace StudioX.Application;

using StudioX.Application.Arduino;
using StudioX.Packages;

/// <summary>组合器件原生模板和框架模板，芯片选择仍由通用新建工程流程负责。</summary>
public static class ZephyrTemplateCatalog
{
    public static bool IsZephyr(ProjectTemplate? template) => template?.Id == ZephyrDeviceCatalog.TemplateId;
    public static IReadOnlyList<ProjectTemplate> Templates(DeviceDefinition device)
    {
        var templates = ArduinoTemplateCatalog.Templates(device);
        return ZephyrDeviceCatalog.ForDevice(device) is null || templates.Any(IsZephyr) ? templates
            : [.. templates, new(ZephyrDeviceCatalog.TemplateId, "Zephyr", Description(device), "src/main.c",
                DevelopmentComponents: [ZephyrDeviceCatalog.Requirement])];
    }

    public static string Description(DeviceDefinition device) =>
        $"Zephyr {ZephyrDeviceCatalog.ZephyrVersion} · {device.Id}\n" +
        "生成工程自带的自定义板级配置，使用内部时钟和最小线程示例。\n" +
        "请按自己的电路调整 boards/ 下的 DTS、app.overlay 和 prj.conf；不预设 LED、串口或外部晶振。\n" +
        "编译使用锁定的 Zephyr 开发组件，下载与调试需另行适配实际板卡。";
}
