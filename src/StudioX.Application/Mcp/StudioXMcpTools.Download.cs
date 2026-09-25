namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>下载只接受当前工程经验证的单一构建产物，不接受模型指定的任意磁盘映像。</summary>
public sealed partial class StudioXMcpTools
{
    [McpServerTool(Name = "firmware_download_plan")]
    [Description("只读预检当前工程唯一的已编译固件、芯片、SHA-256 和可用烧录器；不创建快照、不连接硬件。OpenOCD 工程适用。")]
    public async Task<string> FirmwareDownloadPlanAsync(
        [Description("可选烧录器 ID；省略时使用工程下载设置。")]
        string? probeId = null,
        [Description("可选 SWD/JTAG 速度 kHz；省略时使用工程下载设置。")]
        int? speedKhz = null,
        [Description("可选烧录器序列号；省略时使用工程下载设置。")]
        string? serial = null,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var configuration = await Services.Downloads.ConfigurationAsync(Project, cancellationToken).ConfigureAwait(false)
            ?? throw new StudioXException("DOWNLOAD_UNSUPPORTED", "当前工程没有 OpenOCD 下载配置。");
        var selected = SelectDownloadOptions(configuration, probeId, speedKhz, serial);
        var preview = await Services.Downloads.PreviewAsync(Project, selected, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            deviceId = preview.Configuration.Device.Id,
            image = Path.GetRelativePath(Project, preview.SourceImage).Replace('\\', '/'),
            imageFormat = preview.Format,
            imageSha256 = preview.Sha256,
            imageBytes = preview.ImageBytes,
            probeId = selected.ProbeId,
            speedKhz = selected.SpeedKhz,
            serial = selected.Serial,
            probes = configuration.OpenOcd.Probes.Select(probe => new
            {
                probe.Id, probe.DisplayName, probe.DefaultSpeedKhz
            }),
            requiresApproval = true,
            operation = "经逐次授权后，OpenOCD 将核对目标身份，按固件映像范围擦写、回读校验并复位运行。"
        });
    }

    [McpServerTool(Name = "firmware_download")]
    [Description("逐次授权后将当前工程已编译的固件下载到实机并校验。必须填写预检返回的准确芯片 ID、固件 SHA-256、烧录器 ID 和速度；不自动编译、不接受任意文件路径。")]
    public async Task<string> FirmwareDownloadAsync(
        [Description("firmware_download_plan 返回的准确芯片 ID。")]
        string deviceId,
        [Description("firmware_download_plan 返回的完整 64 位十六进制 SHA-256。")]
        string imageSha256,
        [Description("firmware_download_plan 返回的烧录器 ID。")]
        string probeId,
        [Description("烧录器时钟速度，单位 kHz。")]
        int speedKhz,
        [Description("可选烧录器序列号；须与预检选项一致。")]
        string? serial = null,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        EnsureDebugCanStart();
        if (string.IsNullOrWhiteSpace(deviceId) || imageSha256 is null || imageSha256.Length != 64 ||
            !imageSha256.All(Uri.IsHexDigit))
            throw new StudioXException("DOWNLOAD_APPROVAL", "需要预检返回的准确芯片型号及完整固件 SHA-256。");
        var configuration = await Services.Downloads.ConfigurationAsync(Project, cancellationToken).ConfigureAwait(false)
            ?? throw new StudioXException("DOWNLOAD_UNSUPPORTED", "当前工程没有 OpenOCD 下载配置。");
        var selected = new DownloadOptions(probeId, speedKhz, serial);
        var preview = await Services.Downloads.PreviewAsync(Project, selected, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.Configuration.Device.Id, deviceId, StringComparison.Ordinal) ||
            !string.Equals(preview.Sha256, imageSha256, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("DOWNLOAD_APPROVAL_CHANGED", "芯片型号或当前固件与预检值不一致，请重新读取下载计划。");
        var probe = configuration.OpenOcd.Probes.Single(item => item.Id == selected.ProbeId);
        var image = Path.GetRelativePath(Project, preview.SourceImage).Replace('\\', '/');
        await RequireApprovalAsync("firmware_download",
            $"将 {image}（SHA-256 {preview.Sha256}，{preview.ImageBytes} 字节）写入 {deviceId}；烧录器 {probe.DisplayName}"
                + $" / {selected.SpeedKhz} kHz" + (string.IsNullOrEmpty(serial) ? "" : $" / 序列号 {serial}")
                + "。将核对目标、按映像范围擦写、校验并复位运行；不执行整片擦除或选项字节修改。",
            StudioXMcpPermission.FirmwareDownload, cancellationToken).ConfigureAwait(false);
        EnsureDebugCanStart();
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var report = await Services.Downloads.DownloadApprovedAsync(Project, selected, deviceId, imageSha256,
            token: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            report.Success, report.ExitCode, report.TimedOut, report.LogPath,
            log = LimitOutput(report.Log, 16_000),
            message = report.Summary
        });
    }

    private static DownloadOptions SelectDownloadOptions(DownloadConfiguration configuration,
        string? probeId, int? speedKhz, string? serial) =>
        new(probeId ?? configuration.Options.ProbeId, speedKhz ?? configuration.Options.SpeedKhz,
            serial ?? configuration.Options.Serial);
}
