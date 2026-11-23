namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

public static class Ag32CallbackSource
{
    public static (string File, string Signature) Resolve(string name, string generated)
    {
        var file = ModuleFile(name);
        if (!Ag32SystemSupport.IsManagedFile(file, generated))
        {
            throw new StudioXException("AG32_CALLBACK", "请先保存并生成对应外设。");
        }
        var signature = Regex.Match(generated, @"__attribute__\(\(weak\)\) (void " + Regex.Escape(name) + @"\([^;{}\r\n]*\))\s*\{");
        if (!signature.Success)
        {
            throw new StudioXException("AG32_CALLBACK", $"当前配置没有生成 {name}，请检查中断事件。");
        }
        return (file, signature.Groups[1].Value);
    }

    public static string ModuleFile(string name)
    {
        if (Regex.IsMatch(name, @"\ASX_(EXT_INT[0-7]|LOCAL_INT[0-3])_Interrupt\z"))
        {
            return "src/EXTIRQ.c";
        }
        if (name == "SX_DMA_OtherChannels")
        {
            return "src/SPI.c";
        }
        if (Regex.IsMatch(name, @"\ASX_DMA_CH[0-7]_(Complete|Error)\z"))
        {
            return "src/DMA.c";
        }
        foreach (var unit in new[] { "USB0", "MAC0", "FLASH", "FCB0" })

        {
            if (name == $"SX_{unit}_Interrupt")
            {
                return $"src/{Ag32Setup.ControllerModule(unit)}.c";
            }
        }
        if (name == "SX_RTC_Interrupt")
        {
            return "src/RTC.c";
        }
        if (name == "SX_WDOG_Warning")
        {
            return "src/WDOG.c";
        }
        var match = Regex.Match(name, @"\ASX_(GPIO|UART|GPTIMER|TIMER|I2C|CAN|SPI)\d+_[A-Za-z]+\z");
        if (!match.Success)
        {
            throw new StudioXException("AG32_CALLBACK", "无法识别当前回调。");
        }
        var module = match.Groups[1].Value;
        return $"src/{(module.EndsWith("TIMER", StringComparison.Ordinal) ? "TIM" : module)}.c";
    }

    public static int Find(string source, string name) => Ag32MainInitialization.FindCallback(source, name);
}
