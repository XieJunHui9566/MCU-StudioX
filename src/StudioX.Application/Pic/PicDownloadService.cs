namespace StudioX.Application.Pic;

using StudioX.Devices;
using StudioX.Engine.Pic;

/// <summary>协调 USB 所有权，界面仅通过应用服务访问 PIC 下载流程。</summary>
public sealed class PicDownloadService(StudioX.Engine.Pic.PicDownloadService engine, DeviceHub devices)
{
    public Task<PicDownloadSettings> LoadSettingsAsync(string root, CancellationToken token = default) => engine.LoadSettingsAsync(root, token);
    public Task SaveSettingsAsync(string root, string serial, bool release, CancellationToken token = default) => engine.SaveSettingsAsync(root, serial, release, token);
    public Task<PicProgrammer[]> ListAsync(IProgress<string>? output = null, CancellationToken token = default) => engine.ListAsync(output, token);
    public Task<PicDownloadPreview> PreviewAsync(string root, CancellationToken token = default) => engine.PreviewAsync(root, token);

    public async Task<PicDownloadReport> DownloadApprovedAsync(string root, string approvalId, IProgress<string>? output = null, CancellationToken token = default)
    {
        var settings = await engine.LoadSettingsAsync(root, token);
        settings.Validate();
        using var ownership = await devices.ReserveAsync("usb:pickit3:" + settings.ProgrammerSerial, token);
        return await engine.DownloadApprovedAsync(root, approvalId, output, token);
    }
}
