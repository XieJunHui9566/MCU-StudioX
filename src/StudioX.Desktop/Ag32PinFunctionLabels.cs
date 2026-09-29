namespace StudioX.Desktop;

/// <summary>保留 VE 原始信号名，同时解释恒定输出与实际供电脚的区别。</summary>
internal static class Ag32PinFunctionLabels
{
    internal static string Display(string name) => name switch
    {
        "VCC" => "固定高电平（VCC）",
        "GND" => "固定低电平（GND）",
        _ => name
    };

    internal static string Compact(string name) => name switch
    {
        "VCC" => "恒高 1",
        "GND" => "恒低 0",
        _ => name
    };

    internal static string? Description(string name) => name switch
    {
        "VCC" => "逻辑配置使普通 IO 持续输出高电平（1）；不是电源引脚，也不是上拉电阻。VE 中仍使用 VCC，下载映射后生效。",
        "GND" => "逻辑配置使普通 IO 持续输出低电平（0）；不是接地引脚，也不是下拉电阻。VE 中仍使用 GND，下载映射后生效。",
        _ => null
    };
}
