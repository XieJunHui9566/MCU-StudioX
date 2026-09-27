namespace StudioX.Application;

using StudioX.Devices;
using StudioX.Engine;

/// <summary>协调串口所有权与 ESP 下载；Desktop 和 MCP 使用同一用例服务。</summary>
public sealed class EspressifDownloadService(EspressifFlashService flash, DeviceHub devices)
{
    public Task<EspressifFlashConfiguration?> ConfigurationAsync(string project, CancellationToken token = default) =>
        flash.ConfigurationAsync(project, token);

    public Task<EspressifFlashSettings> LoadSettingsAsync(string project, CancellationToken token = default) =>
        EspressifFlashSettings.ReadAsync(project, token);

    public Task SaveSettingsAsync(string project, EspressifFlashSettings settings, CancellationToken token = default) =>
        flash.SaveSettingsAsync(project, settings, token);

    public Task<EspressifFlashPreview> PreviewAsync(string project, EspressifFlashSettings settings, CancellationToken token = default) =>
        flash.PreviewAsync(project, settings, token);

    public async Task<EspressifFlashReport> DownloadApprovedAsync(string project, EspressifFlashSettings settings,
        string deviceId, string layoutSha256, IProgress<string>? output = null, CancellationToken token = default)
    {
        settings.Validate(requirePort: true);
        using var ownership = await devices.ReserveAsync("serial:" + settings.Port, token);
        return await flash.DownloadApprovedAsync(project, settings, deviceId, layoutSha256, output, token);
    }
}
