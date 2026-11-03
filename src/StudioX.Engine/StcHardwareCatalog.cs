namespace StudioX.Engine;

using System.Text.RegularExpressions;

/// <summary>按 stcgal 1.10 协议选项类声明能力；实机选项类在擦除前再次核对。</summary>
public static class StcHardwareCatalog
{
    private static readonly StcHardwareValue[] Boolean = [new("true", "启用"), new("false", "禁用")];
    private static StcHardwareField Flag(string id, string label) => new(id, label, Boolean);
    private static StcHardwareField Choice(string id, string label, params string[] values) => new(id, label,
        values.Select(value => new StcHardwareValue(value, value switch
        {
            "high" => "高",
            "low" => "低",
            "mid" => "中",
            "long" => "较长",
            "short" => "较短",
            "normal" => "普通模式",
            "push-pull" => "推挽模式",
            _ => value
        })).ToArray());

    public static string Profile(string deviceId)
    {
        var id = deviceId.ToUpperInvariant();
        // 顺序与后端自动选择一致，小容量 STC15A / STC12A 不能套用完整 STC15 / STC12。
        if (Regex.IsMatch(id, @"^(STC|IAP|IRC)(12H|15H)"))
        {
            return "unqualified";
        }
        if (Regex.IsMatch(id, @"^STC(89|90)(C|LE)\d"))
        {
            return "89";
        }
        if (Regex.IsMatch(id, @"^STC12(C|LE)\d052"))
        {
            return "12a";
        }
        if (Regex.IsMatch(id, @"^(STC|IAP)(10|11|12)\D"))
        {
            return "12";
        }
        if (Regex.IsMatch(id, @"^(STC|IAP)15[FL][012]0\d(E|EA|)$"))
        {
            return "15a";
        }
        if (Regex.IsMatch(id, @"^(STC|IAP|IRC)15\D"))
        {
            return "15";
        }
        if (id.StartsWith("STC8", StringComparison.Ordinal))
        {
            return "8";
        }
        return "unqualified";
    }

    public static IReadOnlyList<StcHardwareField> For(string deviceId)
    {
        var profile = Profile(deviceId);
        if (profile == "unqualified")
        {
            return [];
        }
        var fields = new List<StcHardwareField>();
        if (profile != "12a")
        {
            fields.Add(Flag("reset_pin_enabled", "复位引脚使能（禁用时用作 I/O）"));
        }
        if (profile is "89" or "12" or "15" or "8")
        {
            fields.Add(Choice("clock_gain", "振荡器放大增益", "low", "high"));
        }
        fields.Add(Flag("watchdog_por_enabled", "上电复位后由硬件自动启动看门狗"));
        if (profile != "89")
        {
            fields.Add(Flag("watchdog_stop_idle", "空闲状态时停止看门狗计数"));
            fields.Add(Choice("watchdog_prescale", "看门狗定时器分频系数", "2", "4", "8", "16", "32", "64", "128", "256"));
            fields.Add(profile == "12a" ? Choice("low_voltage_reset", "低压检测档位", "low", "high") : Flag("low_voltage_reset", "允许低压复位（禁止低压中断）"));
        }
        if (profile is "15a" or "15" or "8")
        {
            fields.Add(new("low_voltage_threshold", "低压检测档位（电压值依准确型号手册）",
                Enumerable.Range(0, profile == "8" ? 4 : 8).Select(i => new StcHardwareValue(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"档位 {i}")).ToArray()));
        }
        if (profile is "15a" or "15")
        {
            fields.Add(Flag("eeprom_lvd_inhibit", "低压时禁止 EEPROM 操作"));
        }
        fields.Add(Flag("eeprom_erase_enabled", "下次下载用户程序时擦除用户 EEPROM 区"));
        fields.Add(Flag("bsl_pindetect_enabled", "下次冷启动须满足下载引脚条件才能下载"));
        if (profile is "12" or "15" or "8")
        {
            fields.Add(Choice("por_reset_delay", "上电复位延时", "short", "long"));
        }
        if (profile is "15" or "8")
        {
            fields.Add(Choice("rstout_por_state", profile == "15" ? "P3.3 上电复位后输出电平" : "P2.0 上电复位后输出电平", "low", "high"));
            fields.Add(Flag("uart2_passthrough", "UART2 透传模式"));
            fields.Add(Choice("uart2_pin_mode", "UART2 引脚模式", "normal", "push-pull"));
        }
        if (profile == "15")
        {
            fields.Add(Choice("cpu_core_voltage", "CPU 核心电压（须实机提供此字段）", "low", "mid", "high"));
        }
        if (profile == "8")
        {
            fields.Add(Flag("uart1_remap", "UART1 引脚重映射"));
            fields.Add(Flag("epwm_open_drain", "增强 PWM 开漏输出"));
            // 分区依赖实机 IAP 能力和容量；未核验的非 IAP 型号不显示可写入口。
        }
        if (profile == "89")
        {
            fields.RemoveAll(f => f.Id == "reset_pin_enabled");
            fields.Add(Flag("cpu_6t_enabled", "CPU 使用 6T 模式（禁用为 12T）"));
            fields.Add(Flag("ale_enabled", "ALE 输出使能"));
            fields.Add(Flag("xram_enabled", "片内扩展 RAM 使能"));
        }
        if (profile == "12")
        {
            fields.Add(Choice("oscillator_stable_delay", "振荡器稳定等待周期", "4096", "8192", "16384", "32768"));
        }
        return fields;
    }
}
