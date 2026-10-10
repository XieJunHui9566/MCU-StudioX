namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

public sealed partial class StcIspService
{
    /// <summary>制作监控独立于工程构建；仅接受经散列核对的专用编码镜像。</summary>
    public async Task<StcIspPreparation> PrepareMonitorAsync(string directory, string port, byte[] firmware, CancellationToken token = default)
    {
        StcMonitorImage.ValidateSetup(firmware);
        var root = Path.GetFullPath(directory);
        var (_, device) = await ReadProjectDeviceAsync(root, token);
        var settings = new StcIspSettings(Port: port, ClockMode: StcClockMode.Preserve);
        ValidateMonitorTarget(device, settings);
        var tool = await GetToolStatusAsync(token);
        if (!tool.Available || tool.PythonExecutable is null) {throw new StudioXException("STC_ISP_TOOL", tool.Message);}
        var session = PathBoundary.Resolve(root, ".build/stc-isp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var image = Path.Combine(session, "monitor.bin");
        await File.WriteAllBytesAsync(image, firmware, token);
        var script = Path.Combine(session, "studiox-stcgal-guard.py");
        await using (var resource = typeof(StcIspService).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.studiox-stcgal-guard.py")
            ?? throw new StudioXException("STC_ISP_TOOL", "STC ISP 型号防护脚本缺失。"))
        {
            await using var destination = File.Create(script);
            await resource.CopyToAsync(destination, token);
        }
        return new(root, device.Id, device.FlashBytes, port.ToUpperInvariant(), image, image, StcMonitorImage.SetupSha256,
            firmware.Length, firmware.Length - 1, tool.PythonExecutable, script, Path.Combine(session, "stcgal.log"), settings, MonitorSetup: true);
    }

    public Task<StcIspReport> SetupMonitorAsync(StcIspPreparation prepared, IProgress<string>? output = null, CancellationToken token = default)
    {
        if (!prepared.MonitorSetup) {throw new StudioXException("STC_ISP_CHANNEL", "专用制作入口不接受普通用户固件。");}
        return Task.Run(() => DownloadPreparedCoreAsync(prepared, output, token), token);
    }

    private static void ValidateMonitorTarget(DeviceDefinition device, StcIspSettings settings)
    {
        settings.ValidateFor(StcIspCapabilities.For(device), requirePort: true);
        if (device.Id != "IAP15F2K61S2" || device.FlashBytes != 62464 || settings.ClockMode != StcClockMode.Preserve || settings.TransferBaud != 115200)
            {throw new StudioXException("MON51_SETUP_TARGET", "制作通道当前仅核对 IAP15F2K61S2 / 7.2.5S，使用 115200 并保留当前时钟来源。其他目标不会擦写。");}
    }
}
