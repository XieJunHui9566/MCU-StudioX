namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>内置 SDK 的可配置选择；不表示任意板卡都支持列表中的容量或总线速度。</summary>
public sealed record EspressifModuleCapabilities(string Target, int[] FlashSizesMb, string[] FlashModes,
    int[] FlashFrequenciesMhz, string[] PsramModes, string[]? P4RevisionFamilies = null, bool SupportsSingleCore = false)
{
    internal static EspressifModuleCapabilities ForTarget(string target)
    {
        if (target is not ("esp32" or "esp32s3" or "esp32p4" or "esp32c3" or "esp32c5" or "esp32c6" or "esp8266"))
        {
            throw new StudioXException("ESP_MODULE_TARGET", "内置 SDK 未支持此模块目标。");
        }
        // 对应锁定 SDK 的稳定配置组合；120 MHz 需要 HPM/实验特性和 PSRAM 联动，此入口尚未提供这些组合。
        // 已有原生 sdkconfig 可以沿用高频设置，显式模块选择仍须核对 SDK 实际配置。
        var maximumFlashMb = target switch
        {
            "esp32s3" => 128,
            "esp32p4" => 64,
            "esp32c5" => 32,
            _ => 16
        };
        return new(target, new[] { 1, 2, 4, 8, 16, 32, 64, 128 }.Where(size => size <= maximumFlashMb).ToArray(),
            target == "esp32s3" ? ["qio", "qout", "dio", "dout", "opi"] : ["qio", "qout", "dio", "dout"],
            target is "esp32" or "esp32c3" or "esp8266" ? [20, 26, 40, 80] :
            [20, 40, 80],
            target switch
            {
                "esp32" or "esp32c5" => ["disabled", "quad"],
                "esp32s3" => ["disabled", "quad", "octal"],
                "esp32p4" => ["disabled", "hex"],
                _ => ["disabled"]
            },
            target == "esp32p4" ? ["legacy", "current"] : null, target == "esp32");
    }
}
