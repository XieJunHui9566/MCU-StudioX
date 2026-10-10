namespace StudioX.Application.StcDebugging;

using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>从明确选择的器件包读取固件；制作占有串口，写入成功后仍须断电激活监控。</summary>
public sealed class Mon51SetupService(StcIspService isp, DeviceHub devices, PackRepository packs, Func<bool> debugActive)
{
    public async Task<IReadOnlyList<Mon51FirmwareSource>> ListFirmwareSourcesAsync(string project, CancellationToken token = default)
    {
        var manifest = await ProjectService.ReadAsync(project, token);
        if (manifest.DeviceId != "IAP15F2K61S2" || manifest.ToolsetId != "stc.sdcc") {return [];}
        var result = new List<Mon51FirmwareSource>();
        foreach (var pack in await packs.ListCatalogAsync(token))
        {
            foreach (var device in pack.Manifest.Devices.Where(d => d.Id == manifest.DeviceId))
            {
                if (IsSupported(device) && device.MonitorFirmware is { } firmware)
                {
                    result.Add(new(pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash, device.Id,
                        firmware.Id, firmware.Version, firmware.ImageSha256,
                        pack.Manifest.Id == manifest.PackId && pack.Manifest.Version == manifest.PackVersion && pack.ContentHash == manifest.PackContentHash));
                }
            }
        }
        return result;
    }

    public async Task<StcIspPreparation> PrepareAsync(string project, string port, string confirmedTarget,
        Mon51FirmwareSource source, CancellationToken token = default)
    {
        if (debugActive()) {throw new StudioXException("MON51_SETUP_SESSION", "请先结束当前调试再制作仿真芯片。");}
        if (confirmedTarget != "IAP15F2K61S2" || source.DeviceId != confirmedTarget)
            {throw new StudioXException("MON51_SETUP_TARGET", "请明确确认连接的是 IAP15F2K61S2，并选择同型号监控固件。");}
        var selected = (await packs.ListCatalogAsync(token)).SingleOrDefault(p => p.Manifest.Id == source.PackId && p.Manifest.Version == source.PackVersion);
        if (selected is null || selected.ContentHash != source.PackContentHash)
            {throw new StudioXException("MON51_SETUP_PACK", "选中的监控器件包缺失或身份已改变，请重新选择。");}
        // 轻量目录只用于选择；完整校验和镜像快照都在任何串口操作之前完成。
        var verified = await PackRepository.VerifyAsync(selected, token);
        var device = verified.Manifest.Devices.SingleOrDefault(d => d.Id == confirmedTarget);
        if (device is null || !IsSupported(device) || device.MonitorFirmware is not { } firmware ||
            firmware.Id != source.FirmwareId || firmware.Version != source.FirmwareVersion || firmware.ImageSha256 != source.ImageSha256)
            {throw new StudioXException("MON51_SETUP_FIRMWARE", "器件包监控固件不在已核对的制作范围内。");}
        var bytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(verified.RootDirectory, firmware.ImageFile), token);
        StcMonitorImage.ValidateSetup(bytes);
        var prepared = await isp.PrepareMonitorAsync(project, port, bytes, token);
        await JsonStore.WriteAsync(Path.Combine(Path.GetDirectoryName(prepared.Image)!, "monitor-source.json"), source, token);
        return prepared;
    }

    private static bool IsSupported(DeviceDefinition device) => device is { Id: "IAP15F2K61S2", Architecture: "mcs51", FlashBytes: 62464,
        MonitorFirmware: { Id: "stc.mon51.iap15f2k61s2", Protocol: "stc-mon51-encoded", Version: "2.5.0", ImageBytes: 61440,
            BootloaderVersion: StcMonitorImage.Bootloader, BootloaderStatus: 0x70, ImageSha256: StcMonitorImage.SetupSha256 } };

    public async Task<StcIspReport> ExecuteAsync(StcIspPreparation prepared, IProgress<string>? output, CancellationToken token = default)
    {
        if (debugActive()) {throw new StudioXException("MON51_SETUP_SESSION", "当前有调试会话，不能制作仿真芯片。");}
        using var reservation = await devices.ReserveAsync("serial:" + prepared.Port.ToUpperInvariant(), token);
        var result = await isp.SetupMonitorAsync(prepared, output, token);
        if (!result.Success || !result.Log.Contains("STUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED", StringComparison.Ordinal))
            {throw new StudioXException("MON51_SETUP_FAILED", "制作未收到完整成功应答；不能确认监控可用。\n" + result.Log);}
        return result;
    }
}
