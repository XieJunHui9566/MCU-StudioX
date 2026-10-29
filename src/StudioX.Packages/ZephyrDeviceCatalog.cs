namespace StudioX.Packages;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>按准确器件身份和容量提供框架模板，禁止通过相邻料号推断支持。</summary>
public static class ZephyrDeviceCatalog
{
    public const string TemplateId = "zephyr-kernel";
    public const string ZephyrVersion = "4.4.2";
    public const string ZephyrCommit = "dccb09599635bdff17633fa7e9dab014b91dce90";
    public const string ToolsetId = "zephyr.stm32";
    public const string ToolsetVersion = "4.4.2";
    public const string CompilerId = "zephyr-sdk-1.0.1-arm-14.3.0";
    private static readonly Lazy<IReadOnlyList<ZephyrDeviceProfile>> profiles = new(() =>
    {
        using var stream = typeof(ZephyrDeviceCatalog).Assembly.GetManifestResourceStream("StudioX.Packages.ZephyrDeviceCatalog.json")!;
        return JsonSerializer.Deserialize<ZephyrDeviceProfile[]>(stream, JsonStore.Options)!;
    });

    public static IReadOnlyList<ZephyrDeviceProfile> Profiles => profiles.Value;
    public static ZephyrDeviceProfile? ForDevice(DeviceDefinition device) => Profiles.SingleOrDefault(p =>
        p.DeviceId == device.Id && p.Architecture == device.Architecture &&
        p.FlashOrigin == device.FlashOrigin && p.FlashBytes == device.FlashBytes &&
        p.RamOrigin == device.RamOrigin && p.RamBytes == device.RamBytes);
    public static ZephyrDeviceProfile Require(string deviceId) => Profiles.SingleOrDefault(p => p.DeviceId == deviceId)
        ?? throw new StudioXException("ZEPHYR_DEVICE", "此准确器件尚无经过核验的 Zephyr 模板：" + deviceId);
    public static DevelopmentComponentRequirement Requirement => new(ToolsetId, ToolsetVersion, CompilerId, Purpose: "Zephyr 框架与 SDK");
}
