namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>规划并执行明确授权的固件下载；各后端分别核对自己的目标与产物。</summary>
internal sealed partial class FirmwareDownloadMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "firmware_download_plan")]
    [Description("只读预检当前工程已编译固件、芯片与 SHA-256；OpenOCD 返回探针，AG32 基础映射返回 MCU/映射两个镜像的地址和哈希，Espressif 返回 SDK 多映像布局与串口。多镜像的 imageSha256 固定整个布局；不创建快照、不连接硬件。")]
    public async Task<string> FirmwareDownloadPlanAsync(
        [Description("可选烧录器 ID；省略时使用工程下载设置。")]
        string? probeId = null,
        [Description("可选 SWD/JTAG 速度 kHz；省略时使用工程下载设置。")]
        int? speedKhz = null,
        [Description("可选烧录器序列号；省略时使用工程下载设置。")]
        string? serial = null,
        [Description("ESP 下载使用的明确 COM 端口；省略时仅读取工程已保存的端口，不自动选择。")]
        string? port = null,
        [Description("可选 ESP 串口波特率：115200、230400、460800、921600；省略时读取工程设置。")]
        int? baudRate = null,
        CancellationToken cancellationToken = default)
    {
        var project = await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        if (project.Espressif is not null)
        {
            return await EspressifPlanAsync(port, baudRate, cancellationToken).ConfigureAwait(false);
        }
        var configuration = await Services.Downloads.ConfigurationAsync(Project, cancellationToken).ConfigureAwait(false)
            ?? throw new StudioXException("DOWNLOAD_UNSUPPORTED", "当前工程没有 OpenOCD 下载配置。");
        var selected = SelectDownloadOptions(configuration, probeId, speedKhz, serial);
        var preview = await Services.Downloads.PreviewAsync(Project, selected, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            deviceId = preview.Configuration.Device.Id,
            image = Path.GetRelativePath(Project, preview.SourceImage).Replace('\\', '/'),
            imageFormat = preview.Format,
            imageSha256 = preview.ApprovalSha256,
            imageBytes = preview.ImageBytes,
            images = preview.Images.Select(image => new
            {
                image.RelativePath,
                image.Format,
                image.Sha256,
                image.Bytes,
                address = $"0x{image.Address:X8}",
                image.Role
            }),
            probeId = selected.ProbeId,
            speedKhz = selected.SpeedKhz,
            serial = selected.Serial,
            probes = configuration.OpenOcd.Probes.Select(probe => new
            {
                probe.Id,
                probe.DisplayName,
                probe.DefaultSpeedKhz
            }),
            requiresApproval = true,
            operation = preview.Images.Count > 1
                ? "经逐次授权后，OpenOCD 将核对目标身份，按所列 MCU 与 VE 映射两个镜像范围擦写、分别校验并复位运行。imageSha256 固定整个下载布局。"
                : "经逐次授权后，OpenOCD 将核对目标身份，按固件映像范围擦写、回读校验并复位运行。"
        });
    }

    [McpServerTool(Name = "firmware_download")]
    [Description("逐次授权后将当前工程已编译镜像下载到实机并校验；AG32 基础映射同时写入 MCU 固件和映射 BIN。必须复述预检芯片 ID、imageSha256（多镜像为布局哈希）、烧录器 ID 和速度；不自动编译、不接受任意文件路径。")]
    public async Task<string> FirmwareDownloadAsync(
        [Description("firmware_download_plan 返回的准确芯片 ID。")]
        string deviceId,
        [Description("firmware_download_plan 返回的 imageSha256，完整 64 位十六进制；多镜像为整个布局哈希。")]
        string imageSha256,
        [Description("firmware_download_plan 返回的烧录器 ID。")]
        string probeId,
        [Description("OpenOCD 烧录器时钟速度，单位 kHz；ESP 按预检结果传 0，另填 baudRate。")]
        int speedKhz,
        [Description("可选烧录器序列号；须与预检选项一致。")]
        string? serial = null,
        [Description("ESP 必须复述预检返回的明确 COM 端口；OpenOCD 留空。")]
        string? port = null,
        [Description("ESP 必须复述预检返回的 baudRate；OpenOCD 留空。")]
        int? baudRate = null,
        CancellationToken cancellationToken = default)
    {
        var project = await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        if (project.Espressif is not null)
        {
            return await EspressifDownloadAsync(deviceId, imageSha256, probeId, speedKhz, serial, port, baudRate,
                cancellationToken).ConfigureAwait(false);
        }
        Context.Debug.EnsureCanStart();
        if (string.IsNullOrWhiteSpace(deviceId) || imageSha256 is null || imageSha256.Length != 64 ||
            !imageSha256.All(Uri.IsHexDigit))
        {
            throw new StudioXException("DOWNLOAD_APPROVAL", "需要预检返回的准确芯片型号及完整固件 SHA-256。");
        }
        var configuration = await Services.Downloads.ConfigurationAsync(Project, cancellationToken).ConfigureAwait(false)
            ?? throw new StudioXException("DOWNLOAD_UNSUPPORTED", "当前工程没有 OpenOCD 下载配置。");
        var selected = new DownloadOptions(probeId, speedKhz, serial);
        var preview = await Services.Downloads.PreviewAsync(Project, selected, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.Configuration.Device.Id, deviceId, StringComparison.Ordinal) ||
            !string.Equals(preview.ApprovalSha256, imageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("DOWNLOAD_APPROVAL_CHANGED", "芯片型号或当前固件与预检值不一致，请重新读取下载计划。");
        }
        var probe = configuration.OpenOcd.Probes.Single(item => item.Id == selected.ProbeId);
        var imageDetails = preview.Images.Count > 0
            ? string.Join("\n", preview.Images.Select(image =>
                $"{image.Role}：{image.RelativePath} · 0x{image.Address:X8} · {image.Bytes} 字节 · SHA-256 {image.Sha256}"))
            : $"固件：{Path.GetRelativePath(Project, preview.SourceImage)} · {preview.ImageBytes} 字节 · SHA-256 {preview.Sha256}";
        await RequireApprovalAsync("firmware_download",
            $"写入 {deviceId}：\n{imageDetails}\n布局 SHA-256 {preview.ApprovalSha256}\n烧录器 {probe.DisplayName}"
                + $" / {selected.SpeedKhz} kHz" + (string.IsNullOrEmpty(serial) ? "" : $" / 序列号 {serial}")
                + "。将核对目标、按映像范围擦写、校验并复位运行；不执行整片擦除或选项字节修改。",
            StudioXMcpPermission.FirmwareDownload, cancellationToken).ConfigureAwait(false);
        Context.Debug.EnsureCanStart();
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var report = await Services.Downloads.DownloadApprovedAsync(Project, selected, deviceId, imageSha256,
            token: cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            report.Success,
            report.ExitCode,
            report.TimedOut,
            report.LogPath,
            log = LimitOutput(report.Log, 16_000),
            message = report.Summary
        });
    }

    private static DownloadOptions SelectDownloadOptions(DownloadConfiguration configuration,
        string? probeId, int? speedKhz, string? serial) =>
        new(probeId ?? configuration.Options.ProbeId, speedKhz ?? configuration.Options.SpeedKhz,
            serial ?? configuration.Options.Serial);
}
