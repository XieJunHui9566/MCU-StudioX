namespace StudioX.Application;

using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record ProjectDeviceInfo(string Manufacturer, string DeviceName, string TemplateName,
    string TemplateDescription, string PackName, string Notice)
{
    public static ProjectDeviceInfo Recorded(ProjectManifest project) => project.Kind switch
    {
        ProjectKind.CubeMx => new("STMicroelectronics", project.DeviceId, "STM32CubeMX 生成工程",
            "保留 CubeMX 生成的工程配置，未使用 StudioX 起始模板。", "未使用 .mcupack", ""),
        ProjectKind.Zephyr => new("未记录", project.Zephyr!.BoardTarget, project.TemplateId,
            "Zephyr 实验模式；板级配置仍待编译与实板验证。", project.PackId, "实验模式：未核实板卡丝印和全部外设。"),
        _ => new("未记录", project.DeviceId, project.TemplateId, "", project.PackId, "")
    };

    /// <summary>只读工程内的元数据；不扫描器件仓库、不校验 SDK，也不更改工程配置。</summary>
    public static async Task<ProjectDeviceInfo> ReadAsync(string directory, ProjectManifest project,
        CancellationToken cancellationToken = default)
    {
        var recorded = Recorded(project);
        if (project.Kind == ProjectKind.CubeMx)
        {
            return recorded;
        }
        if (project.Kind == ProjectKind.Zephyr)
        {
            try
            {
                var pack = await JsonStore.ReadAsync<ZephyrPackManifest>(
                    Path.Combine(directory, ".studiox", "zephyr-pack.json"), cancellationToken);
                var board = pack.Boards?.FirstOrDefault(item => item.Id == project.Zephyr!.BoardId &&
                    item.BoardTarget == project.Zephyr.BoardTarget && item.BoardRevision == project.Zephyr.BoardRevision);
                var template = board?.Templates?.FirstOrDefault(item => item.Id == project.TemplateId);
                if (pack.Id != project.PackId || pack.Version != project.PackVersion || board is null || template is null)
                {
                    return recorded with
                    {
                        Notice = "Zephyr 包记录与工程选择不一致；仅显示工程清单中的板级目标。"
                    };
                }
                return new(pack.Vendor, board.DisplayName, template.DisplayName,
                    template.Description, pack.DisplayName,
                    "Zephyr 实验模式；板级配置待实板验证。" +
                    (string.IsNullOrWhiteSpace(board.DocumentationNote) ? "" : "\n" + board.DocumentationNote));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or StudioXException)
            {
                return recorded with
                {
                    Notice = "Zephyr 包说明读取失败，仅显示工程记录。\n" + ex.Message
                };
            }
        }
        try
        {
            var pack = await JsonStore.ReadAsync<PackManifest>(Path.Combine(directory, "device", "manifest.json"), cancellationToken);
            if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion)
            {
                return recorded with
                {
                    Notice = "工程内的器件包说明与创建记录不一致，以下显示工程记录的器件和模板标识。"
                };
            }
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
            return recorded with
            {
                Notice = "无法读取工程内的器件包说明，显示工程记录的标识。\n" + ex.Message
            };
        }
    }
}
