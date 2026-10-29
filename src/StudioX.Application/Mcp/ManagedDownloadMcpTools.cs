namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Foundation;

/// <summary>补齐已接入 IDE 的 Arduino 和 PIC 下载；复用应用层设备租约及不可替换的预检身份。</summary>
internal sealed class ManagedDownloadMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "arduino_upload_plan"), Description("只读预检 Arduino 当前板型、已编译 HEX、SHA-256 和 approvalId，不访问串口、不自动编译。")]
    public async Task<string> ArduinoPlanAsync(CancellationToken cancellationToken = default)
    {
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(await Services.Arduino.PreviewUploadAsync(Project, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "arduino_upload"), Description("授权后通过明确 COM 端口上传预检的 Arduino 固件并校验；须复述 approvalId，不猜端口、不自动编译。")]
    public async Task<string> ArduinoUploadAsync(string approvalId, string port, CancellationToken cancellationToken = default)
    {
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var preview = await Services.Arduino.PreviewUploadAsync(Project, cancellationToken).ConfigureAwait(false);
        if (preview.ApprovalId != approvalId)
        {
            throw new StudioXException("DOWNLOAD_APPROVAL_CHANGED", "板型或固件已变化，请重新预检。");
        }
        await RequireApprovalAsync("arduino_upload", $"将 {preview.Board.DisplayName} 固件写入 {port}，替换现有程序。\nSHA-256 {preview.Sha256}",
            StudioXMcpPermission.FirmwareDownload, cancellationToken).ConfigureAwait(false);
        Context.Debug.EnsureCanStart();
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(await Services.Arduino.UploadApprovedAsync(Project, port, approvalId, token: cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "pic_download_plan"), Description("只读预检 PIC 当前固件、配置字、组件、准确 PICkit3 序列号和 approvalId；不连接目标、不自动编译。")]
    public async Task<string> PicPlanAsync(CancellationToken cancellationToken = default)
    {
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(await Services.PicDownloads.PreviewAsync(Project, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "pic_download"), Description("逐次授权将预检 PIC 固件经所选 PICkit3 写入并校验；须复述 approvalId。保留备份、EEPROM、用户 ID，配置以预检为准；不支持调试。")]
    public async Task<string> PicDownloadAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var preview = await Services.PicDownloads.PreviewAsync(Project, cancellationToken).ConfigureAwait(false);
        if (preview.ApprovalId != approvalId)
        {
            throw new StudioXException("DOWNLOAD_APPROVAL_CHANGED", "PIC 固件或下载设置已变化，请重新预检。");
        }
        await RequireApprovalAsync("pic_download", $"将固件写入 {preview.Firmware.DeviceId}，PICkit3 {preview.Settings.ProgrammerSerial}。\nSHA-256 {preview.Firmware.Sha256}\n配置字及程序将按预检写入并校验。",
            StudioXMcpPermission.FirmwareDownload, cancellationToken).ConfigureAwait(false);
        Context.Debug.EnsureCanStart();
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(await Services.PicDownloads.DownloadApprovedAsync(Project, approvalId, token: cancellationToken).ConfigureAwait(false));
    }
}
