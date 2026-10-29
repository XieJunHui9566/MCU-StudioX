namespace StudioX.Application.Arduino;

using StudioX.Engine.Arduino;
using StudioX.Packages;

/// <summary>在器件已有模板旁提供框架选项；不修改器件包，也不按搜索文本猜测板型。</summary>
public static class ArduinoTemplateCatalog
{
    public const string TemplateId = "arduino-sketch";
    public static bool IsArduino(ProjectTemplate? template) => template?.Id == TemplateId;
    public static IReadOnlyList<ProjectTemplate> Templates(DeviceDefinition device)
    {
        var boards = ArduinoBoard.ForDevice(device);
        if (boards.Count == 0 || device.Templates.Any(IsArduino))
        {
            return device.Templates;
        }
        return [.. device.Templates, new(TemplateId, "Arduino", "Arduino sketch（setup / loop）；选择下方明确的开发板配置，框架、工具和库版本按工程锁定。", "Sketch.ino")];
    }
    public static string Description(ArduinoBoard? board) => board is null ? "请选择实际开发板配置；不会根据芯片型号自动猜测 Flash、PSRAM 或引导程序。" :
        board.DisplayName + "\n" + board.Platform.DisplayName + " / CLI " + ArduinoPlatform.CliVersion +
        "\n" + board.Fqbn + "\n可先创建和编辑；编译需要对应的固定开发组件。实板配置仍需按实际模块核实。";
}
