namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>按厂商已核实料号查找 MCU 与封装目标；未知器件不通过名称前缀自动开放。</summary>
public static class Ag32DeviceCatalog
{
    public const string ManualSourceUrl = "https://www.agm-micro.com/upload/userfiles/files/AG32%20MCU%20Reference%20Manual%2820260601%E4%BF%AE%E8%AE%A2%E7%89%88%EF%BC%89.pdf";
    public const string HyperRamTargetSourceUrl = "https://www.ag32mcu.com/wp-content/uploads/2025/06/MANUAL_AG32VH_HyperRAM.pdf";

    // 2026-06-01 手册第 12 页列出这七个准确料号、容量和封装。
    // VH 系列应用指南第 3 页明确 HyperRAM 目标；不把普通 L64/L100 目标用于 VH 封装。
    private static readonly Ag32DeviceProfile[] Profiles =
    [
        new("AG32VF303KCU6", "AGRV2KQ32", "QFN32", 32, 256 * 1024, SourceUrl: ManualSourceUrl),
        new("AG32VF303CCT6", "AGRV2KL48", "LQFP48", 48, 256 * 1024, SupportsVerifiedDownload: true, SourceUrl: ManualSourceUrl),
        new("AG32VF303VCT6", "AGRV2KL100", "LQFP100", 100, 256 * 1024, SourceUrl: ManualSourceUrl),
        new("AG32VH303RCT6", "AGRV2KL64H", "LQFP64", 64, 256 * 1024, ExternalPsramBytes: 8 * 1024 * 1024,
            SourceUrl: ManualSourceUrl, TargetSourceUrl: HyperRamTargetSourceUrl),
        new("AG32VF407RGT6", "AGRV2KL64", "LQFP64", 64, 1024 * 1024, SourceUrl: ManualSourceUrl),
        new("AG32VF407VGT6", "AGRV2KL100", "LQFP100", 100, 1024 * 1024, SourceUrl: ManualSourceUrl),
        new("AG32VH407VGT6", "AGRV2KL100H", "LQFP100", 100, 1024 * 1024, ExternalPsramBytes: 8 * 1024 * 1024,
            SourceUrl: ManualSourceUrl, TargetSourceUrl: HyperRamTargetSourceUrl)
    ];

    public static IReadOnlyList<Ag32DeviceProfile> All => Array.AsReadOnly(Profiles);

    public static Ag32DeviceProfile? Find(string deviceId)
    {
        return Profiles.FirstOrDefault(profile => string.Equals(profile.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    public static Ag32DeviceProfile Require(string deviceId)
    {
        return Find(deviceId) ?? throw new StudioXException("AG32_DEVICE_UNVERIFIED",
            $"没有核实 AGM 器件 {deviceId} 的封装、容量和逻辑目标，不能使用 AG32 映射流程。");
    }

    public static bool Matches(DeviceDefinition device) => Find(device.Id)?.Matches(device) == true;
}
