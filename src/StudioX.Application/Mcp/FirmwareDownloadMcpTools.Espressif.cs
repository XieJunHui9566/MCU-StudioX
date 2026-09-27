namespace StudioX.Application.Mcp;

using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

internal sealed partial class FirmwareDownloadMcpTools
{
    private async Task<string> EspressifPlanAsync(string? port, int? baudRate, CancellationToken token)
    {
        var saved = await Services.EspressifDownloads.LoadSettingsAsync(Project, token).ConfigureAwait(false);
        var settings = saved with
        {
            Port = port?.Trim().ToUpperInvariant() ?? saved.Port,
            BaudRate = baudRate ?? saved.BaudRate
        };
        var preview = await Services.EspressifDownloads.PreviewAsync(Project, settings, token).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            deviceId = preview.Configuration.Device.Id,
            target = preview.Layout.Target,
            imageSha256 = preview.Layout.LayoutSha256,
            hashScope = "完整 SDK 下载布局、Flash 参数和全部 BIN；每个 BIN 的 SHA-256 同时列出",
            images = preview.Layout.Images.Select(image => new { image.RelativePath, offset = $"0x{image.Offset:x}", image.Sha256, image.Bytes }),
            imageBytes = preview.Layout.ImageBytes,
            probeId = "esptool",
            speedKhz = 0,
            port = settings.Port,
            baudRate = settings.BaudRate,
            preview.Layout.FlashMode,
            preview.Layout.FlashFrequency,
            preview.Layout.FlashSize,
            preview.Layout.UseStub,
            preview.EsptoolVersion,
            requiresApproval = true,
            operation = "经逐次授权后，esptool 将核对芯片、进入串口下载模式、按映像所在扇区擦写、逐个校验并复位运行。SDK 配置启用 stub 时会临时装载 RAM 下载器；不执行整片擦除、eFuse 写入或隐式构建。"
        });
    }

    private async Task<string> EspressifDownloadAsync(string deviceId, string layoutSha256, string probeId,
        int speedKhz, string? serial, string? port, int? baudRate, CancellationToken token)
    {
        Context.Debug.EnsureCanStart();
        if (probeId != "esptool" || speedKhz != 0 || !string.IsNullOrEmpty(serial) || port is null || baudRate is null ||
            string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(layoutSha256) ||
            layoutSha256.Length != 64 || !layoutSha256.All(Uri.IsHexDigit))
        {
            throw new StudioXException("ESP_FLASH_APPROVAL", "ESP 下载必须复述计划中的器件、布局 SHA-256、probeId=esptool、speedKhz=0、COM 端口和 baudRate。");
        }
        var settings = new EspressifFlashSettings(Port: port.Trim().ToUpperInvariant(), BaudRate: baudRate.Value);
        var preview = await Services.EspressifDownloads.PreviewAsync(Project, settings, token).ConfigureAwait(false);
        if (preview.Configuration.Device.Id != deviceId || !preview.Layout.LayoutSha256.Equals(layoutSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("ESP_FLASH_APPROVAL_CHANGED", "器件或多映像布局与下载计划不一致，请重新预检。");
        }
        var images = string.Join("；", preview.Layout.Images.Select(image =>
            $"{image.RelativePath} @ 0x{image.Offset:x}，{image.Bytes} 字节，SHA-256 {image.Sha256}"));
        await RequireApprovalAsync("firmware_download", $"将 {deviceId} / {preview.Layout.Target} 的 {preview.Layout.Images.Length} 个 SDK 映像写入 " +
            $"{settings.Port} / {settings.BaudRate} baud；Flash {preview.Layout.FlashSize} / {preview.Layout.FlashMode} / {preview.Layout.FlashFrequency}。" +
            $"布局 SHA-256 {layoutSha256}。{images}。按对应扇区擦写、逐个校验并复位运行；" +
            (preview.Layout.UseStub ? "会临时装载 RAM 下载器。" : "使用芯片 ROM 下载器。"),
            StudioXMcpPermission.FirmwareDownload, token).ConfigureAwait(false);
        Context.Debug.EnsureCanStart();
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var report = await Services.EspressifDownloads.DownloadApprovedAsync(Project, settings, deviceId, layoutSha256,
            token: token).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            report.Success,
            report.ExitCode,
            report.TimedOut,
            report.LogPath,
            log = LimitOutput(report.Log),
            message = report.Summary
        });
    }
}
