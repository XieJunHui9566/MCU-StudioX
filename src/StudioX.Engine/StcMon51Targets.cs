namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>Mon51 V2.5 的准确型号、制作镜像和证据边界；F/L 镜像不能互换。</summary>
public static class StcMon51Targets
{
    public const string HighVoltageDevice = "IAP15F2K61S2";
    public const string LowVoltageDevice = "IAP15L2K61S2";
    public const string DisplayNames = "IAP15F2K61S2 / IAP15L2K61S2";
    public const string LowVoltageSetupSha256 = "D2320E17FE8961813D8A012C46571390D6AA3BA683A594DD05697AD5B990D729";
    public static IReadOnlyList<string> DeviceIds { get; } = Array.AsReadOnly(new[] { HighVoltageDevice, LowVoltageDevice });
    public static bool Supports(string? deviceId) => deviceId is HighVoltageDevice or LowVoltageDevice;
    public static string SetupSha256(string deviceId) => deviceId switch
    {
        HighVoltageDevice => StcMonitorImage.SetupSha256,
        LowVoltageDevice => LowVoltageSetupSha256,
        _ => throw new StudioXException("MON51_TARGET", "Mon51 V2.5 支持的准确型号为 " + DisplayNames + "。")
    };
    public static string Evidence(string deviceId) => deviceId switch
    {
        LowVoltageDevice => "IAP15L2K61S2：Mon51 V2.5 / ISP 7.2.5S / 状态 70 已有实板证据；其他组合未验证。供电范围 2.4–3.6 V。",
        HighVoltageDevice => "IAP15F2K61S2：已有 Mon51 V2.5 实板证据；制作限定 ISP 7.2.5S / 状态标记 70。",
        _ => "此准确型号尚未接入 Mon51 调试。"
    };
}
