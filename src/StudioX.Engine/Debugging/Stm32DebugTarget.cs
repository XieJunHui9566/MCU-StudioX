namespace StudioX.Engine.Debugging;

using StudioX.Packages;

/// <summary>与下载共用精确器件目录；完整订货后缀不影响内核选择，不凭 STM32 前缀放行未知芯片。</summary>
public static class Stm32DebugTarget
{
    public static DebugTargetProfile? Find(DeviceDefinition device)
    {
        var profile = Stm32DownloadCatalog.FindProfile(device.Id);
        if (profile is null || device.Architecture != "arm" || profile.Architecture != device.Architecture ||
            profile.FlashOrigin != device.FlashOrigin || profile.FlashBytes != device.FlashBytes ||
            profile.RamOrigin != device.RamOrigin || profile.RamBytes != device.RamBytes) return null;
        return profile.Id.StartsWith("STM32F1", StringComparison.Ordinal)
            ? new(profile.Id, "Cortex-M3", false)
            : profile.Id.StartsWith("STM32F4", StringComparison.Ordinal) ? new(profile.Id, "Cortex-M4", true) : null;
    }
}
