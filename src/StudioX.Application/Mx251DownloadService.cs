namespace StudioX.Application;

using StudioX.Ai8051Isp;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>下载用例持有构建维护门与设备预约，界面仅提交已选择的快照。</summary>
public sealed class Mx251DownloadService(Mx251IspService isp, BuildService builds, DeviceHub devices)
{
    public Task<IReadOnlyList<HidDevice>> ListDevicesAsync(CancellationToken token = default) => isp.ListDevicesAsync(token);
    public Task<Mx251IspSettings> LoadSettingsAsync(string root, CancellationToken token = default) => isp.LoadSettingsAsync(root, token);
    public async Task SaveSettingsAsync(string root, Mx251IspSettings settings, CancellationToken token = default)
    {
        using var maintenance = builds.AcquireMaintenance(token);
        await isp.SaveSettingsAsync(root, settings, token);
    }
    public async Task<Mx251DownloadDeviceChoice> ResolveDeviceAsync(string root, string? selectedPath = null, CancellationToken token = default)
    {
        var saved = await isp.LoadSettingsAsync(root, token);
        var connected = await isp.ListDevicesAsync(token);
        if (connected.Count == 0)
        {
            throw new StudioXException("MX251_ISP_DEVICE", "未检测到 AI8H2K12G USB 下载器，请连接大底板 USB1 后再次点击下载。");
        }
        var requested = string.IsNullOrWhiteSpace(selectedPath) ? saved.DevicePath : selectedPath;
        if (requested.Length > 0)
        {
            var match = connected.SingleOrDefault(d => d.Path.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return new(saved with
                {
                    DevicePath = match.Path
                }, connected, "", saved);
            }
            // 已绑定的板缺席时不把另一块板当成原板；在下载弹窗选择，无需导航配置页。
            return new(null, connected, "原下载器未连接，请选择本次要下载的设备。", saved);
        }
        return connected.Count == 1 ? new(saved with
        {
            DevicePath = connected[0].Path
        }, connected, "", saved) :
            new(null, connected, "检测到多个 USB 下载器，请选择本次要下载的设备。", saved);
    }
    public async Task<Mx251IspPreparation> PrepareAsync(string root, CancellationToken token = default)
    {
        using var maintenance = builds.AcquireMaintenance(token);
        return await isp.PrepareAsync(root, token);
    }
    public async Task<Mx251IspReport> DownloadPreparedAsync(Mx251IspPreparation prepared, IProgress<string>? output = null, CancellationToken token = default)
    {
        using var maintenance = builds.AcquireMaintenance(token);
        using var ownership = await devices.ReserveAsync("hid:" + prepared.Settings.DevicePath, token);
        return await isp.DownloadPreparedAsync(prepared, output, token);
    }
}
