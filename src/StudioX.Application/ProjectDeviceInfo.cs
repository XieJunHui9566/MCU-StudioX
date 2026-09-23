namespace StudioX.Application;

using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record ProjectDeviceInfo(string Manufacturer, string DeviceName, string TemplateName,
    string TemplateDescription, string PackName, string Notice)
{
    public static ProjectDeviceInfo Recorded(ProjectManifest project) => project.Kind == ProjectKind.CubeMx
        ? new("STMicroelectronics", project.DeviceId, "STM32CubeMX 生成工程",
            "保留 CubeMX 生成的工程配置，未使用 StudioX 起始模板。", "未使用 .mcupack", "")
        : new("未记录", project.DeviceId, project.TemplateId, "", project.PackId, "");

    /// <summary>只读工程内的元数据；不扫描器件仓库、不校验 SDK，也不更改工程配置。</summary>
    public static async Task<ProjectDeviceInfo> ReadAsync(string directory, ProjectManifest project,
        CancellationToken cancellationToken = default)
    {
        var recorded = Recorded(project);
        if (project.Kind == ProjectKind.CubeMx) return recorded;
        try
        {
            var pack = await JsonStore.ReadAsync<PackManifest>(Path.Combine(directory, "device", "manifest.json"), cancellationToken);
            if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion)
                return recorded with { Notice = "工程内的器件包说明与创建记录不一致，以下显示工程记录的器件和模板标识。" };
            var device = pack.Devices?.FirstOrDefault(d => d.Id == project.DeviceId);
            var template = device?.Templates?.FirstOrDefault(t => t.Id == project.TemplateId);
            return recorded with
            {
                Manufacturer = pack.Vendor == "STMicroelectronics / StudioX templates" ? "STMicroelectronics" : pack.Vendor,
                DeviceName = device?.DisplayName ?? project.DeviceId,
                TemplateName = template?.DisplayName ?? project.TemplateId,
                TemplateDescription = template?.Description ?? "",
                PackName = pack.DisplayName,
                Notice = template is null ? "器件包说明缺少当前器件或模板，缺失项显示工程记录的标识。" : ""
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or StudioXException)
        {
            return recorded with { Notice = "无法读取工程内的器件包说明，显示工程记录的标识。\n" + ex.Message };
        }
    }
}
