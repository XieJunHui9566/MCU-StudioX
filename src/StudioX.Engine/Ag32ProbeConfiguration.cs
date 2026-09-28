namespace StudioX.Engine;

using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>AG32 沿用厂商 OpenOCD 目标，只规范化外部 SWD 探针的选择。</summary>
internal static class Ag32ProbeConfiguration
{
    public static bool IsSupported(DeviceDefinition device) => DebugTargetProfile.Find(device)?.IsAg32 == true;

    public static bool IsSupportedProbe(DebugProbeDefinition probe) => probe.Transport == "swd" && (probe.Id switch
        {
            "agm-blaster" or "cmsis-dap" => probe.InterfaceScript == "interface/cmsis-dap.cfg",
            "jlink" => probe.InterfaceScript == "interface/jlink.cfg",
            _ => false
        });

    public static DownloadOptions NormalizeOptions(DeviceDefinition device, DownloadOptions options) =>
        IsSupported(device) && options.ProbeId == "agm-blaster" ? options with { ProbeId = "cmsis-dap" } : options;

    public static DownloadConfiguration Normalize(DownloadConfiguration configuration)
    {
        if (!IsSupported(configuration.Device)) { return configuration; }
        var definition = configuration.OpenOcd;
        if (!Ag32PinMappingTargetScript.IsSupportedPath(definition.TargetScript) ||
            definition.TargetScript != configuration.Device.OpenOcd!.TargetScript || definition.ApplicationFlashBytes != 0x27000 ||
            definition.Probes.Count == 0 || !definition.Probes.All(IsSupportedProbe) ||
            definition.Probes.Select(probe => probe.Id == "agm-blaster" ? "cmsis-dap" : probe.Id).Distinct(StringComparer.Ordinal).Count() != definition.Probes.Count)
        {
            throw new StudioXException("DOWNLOAD_CONFIG", "AG32 需要匹配的厂商目标配置以及 DAP 或 J-Link SWD 接口。");
        }
        // 老工程的 agm-blaster 实际也是 CMSIS-DAP；内存兼容不改写用户器件包和工程文件。
        definition = definition with
        {
            Probes =
            [
                new("cmsis-dap", "DAP-Link (CMSIS-DAP)", "interface/cmsis-dap.cfg", "swd", 1000),
                new("jlink", "J-Link（V9 及以上）", "interface/jlink.cfg", "swd", 1000)
            ]
        };
        return configuration with { OpenOcd = definition, Options = NormalizeOptions(configuration.Device, configuration.Options) };
    }
}
