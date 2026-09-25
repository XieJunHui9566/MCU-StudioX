namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>STC 串口 ISP 只使用当前工程已验证的 Intel HEX；模型不能指定映像路径。</summary>
public sealed partial class StudioXMcpTools
{
    [McpServerTool(Name = "stc_isp_plan")]
    [Description("只读预检 STC 8 位工程的准确型号、已编译 HEX、SHA-256、COM 端口、波特率和时钟选项；不创建快照、不打开串口。")]
    public async Task<string> StcIspPlanAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var settings = await Services.StcIsp.LoadSettingsAsync(Project, cancellationToken).ConfigureAwait(false);
        var preview = await Services.StcIsp.PreviewAsync(Project, settings, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            deviceId = preview.ExpectedModel,
            codeCapacityBytes = preview.ExpectedCodeBytes,
            image = Path.GetRelativePath(Project, preview.SourceImage).Replace('\\', '/'),
            imageSha256 = preview.ImageSha256,
            imageBytes = preview.ImageBytes,
            dataBytes = preview.DataBytes,
            highestAddress = preview.HighestAddress,
            port = preview.Port,
            transferBaud = settings.TransferBaud,
            clockMode = StcClockModeName(settings.ClockMode),
            clockFrequencyHz = settings.ClockFrequencyHz,
            toolAvailable = preview.Tool.Available,
            requiresApproval = true,
            fullErasePossible = true,
            flashReadbackAvailable = false,
            operation = "烧录会按芯片协议擦除现有程序，可能整片擦除，并写入代码和所选时钟选项；STC ISP 不提供原程序备份或 Flash 读回校验。"
        });
    }

    [McpServerTool(Name = "stc_isp_download")]
    [Description("逐次授权后将当前 STC 工程已编译的 HEX 经串口 ISP 写入实机。必须复述预检的型号、SHA-256、COM 口、波特率和时钟设置，明确承认可能整片擦除及无备份/读回；不自动编译、不接受任意映像路径。")]
    public async Task<string> StcIspDownloadAsync(
        [Description("stc_isp_plan 返回的准确芯片型号。")]
        string deviceId,
        [Description("stc_isp_plan 返回的完整 64 位十六进制 HEX SHA-256。")]
        string imageSha256,
        [Description("stc_isp_plan 返回的 COM 端口。")]
        string port,
        [Description("stc_isp_plan 返回的传输波特率。")]
        int transferBaud,
        [Description("stc_isp_plan 返回的 clockMode：preserve、internal_rc 或 external_crystal。")]
        string clockMode,
        [Description("stc_isp_plan 返回的 clockFrequencyHz；为 null 时仍需明确传入 null。")]
        int? clockFrequencyHz,
        [Description("仅在理解原程序会被擦除、部分型号可能整片擦除时传 true。")]
        bool acknowledgeFullErase,
        [Description("仅在理解此流程不提供原程序备份及 Flash 读回校验时传 true。")]
        bool acknowledgeNoReadback,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        ValidateStcIspApprovalFields(deviceId, imageSha256, port, transferBaud, clockMode,
            acknowledgeFullErase, acknowledgeNoReadback);
        var settings = await Services.StcIsp.LoadSettingsAsync(Project, cancellationToken).ConfigureAwait(false);
        var preview = await Services.StcIsp.PreviewAsync(Project, settings, cancellationToken).ConfigureAwait(false);
        if (!StcIspMatchesApproval(preview, deviceId, imageSha256, port, transferBaud,
                clockMode, clockFrequencyHz))
            throw new StudioXException("STC_ISP_APPROVAL_CHANGED",
                "型号、固件、串口或时钟设置与预检值不一致，请重新读取 STC ISP 计划。");

        var image = Path.GetRelativePath(Project, preview.SourceImage).Replace('\\', '/');
        await RequireApprovalAsync("stc_isp_download",
            $"将 {image}（SHA-256 {preview.ImageSha256}，{preview.ImageBytes} 字节）写入 {preview.ExpectedModel}"
            + $" / {preview.Port} / {settings.TransferBaud} baud；时钟 {clockMode}"
            + (clockFrequencyHz is { } frequency ? $" / {frequency} Hz" : " / 保留现有频率")
            + "。将按芯片协议擦除现有程序（可能整片擦除）并写入代码和所选时钟选项；此流程不提供原程序备份或 Flash 读回校验。",
            StudioXMcpPermission.FirmwareDownload, cancellationToken).ConfigureAwait(false);

        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        // 授权后才创建一份快照；再次核对授权的每个值，避免等待期间源码或设置变化。
        var prepared = await Services.StcIsp.PrepareAsync(Project, settings, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(prepared.ImageSha256, imageSha256, StringComparison.OrdinalIgnoreCase) ||
            prepared.ExpectedModel != deviceId || !prepared.Port.Equals(port, StringComparison.OrdinalIgnoreCase) ||
            prepared.Settings != settings ||
            !string.Equals(prepared.PythonExecutable, preview.Tool.PythonExecutable, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("STC_ISP_APPROVAL_CHANGED",
                "批准后固件、目标、串口、时钟或 ISP 工具发生变化；未连接开发板。");
        var report = await Services.StcIsp.DownloadPreparedAsync(prepared, token: cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            report.Success, report.ExitCode, report.TimedOut, report.ModelVerified, report.LogPath,
            log = LimitOutput(report.Log, 16_000),
            message = report.Summary
        });
    }

    private static void ValidateStcIspApprovalFields(string? deviceId, string? imageSha256,
        string? port, int transferBaud, string? clockMode,
        bool acknowledgeFullErase, bool acknowledgeNoReadback)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || imageSha256 is null || imageSha256.Length != 64 ||
            !imageSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(port) ||
            transferBaud is < 1200 or > 921600 ||
            clockMode is not ("preserve" or "internal_rc" or "external_crystal") ||
            !acknowledgeFullErase || !acknowledgeNoReadback)
            throw new StudioXException("STC_ISP_APPROVAL",
                "必须提供预检返回的准确型号、完整 SHA-256、COM 口、波特率、时钟设置，并明确承认可能整片擦除与无读回。");
    }

    private static bool StcIspMatchesApproval(StcIspPreview preview, string deviceId,
        string imageSha256, string port, int transferBaud, string clockMode, int? clockFrequencyHz) =>
        preview.ExpectedModel == deviceId &&
        preview.ImageSha256.Equals(imageSha256, StringComparison.OrdinalIgnoreCase) &&
        preview.Port.Equals(port, StringComparison.OrdinalIgnoreCase) &&
        preview.Settings.TransferBaud == transferBaud &&
        StcClockModeName(preview.Settings.ClockMode) == clockMode &&
        preview.Settings.ClockFrequencyHz == clockFrequencyHz;

    private static string StcClockModeName(StcClockMode mode) => mode switch
    {
        StcClockMode.Preserve => "preserve",
        StcClockMode.InternalRc => "internal_rc",
        StcClockMode.ExternalCrystal => "external_crystal",
        _ => throw new StudioXException("STC_ISP_CLOCK", "未知 STC ISP 时钟模式。")
    };
}
