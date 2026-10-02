namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>结构检查先于转换；离线推荐不能代替当前工程的实际布局布线。</summary>
public static class Ag32ClockPolicy
{
    // 0.5 ns 是界面的余量提醒阈值，不是器件规格，也不改变负余量的下载拦截。
    public const decimal LowMarginThresholdNs = 0.5m;

    public static string? Validate(string deviceId, Ag32PinClockSettings clocks)
    {
        var profile = Ag32DeviceCatalog.Require(deviceId);
        if (clocks.HseMhz is <= 0 || clocks.SysMhz is <= 0 || clocks.BusMhz is < 0)
            return "HSECLK、SYSCLK 必须为正数；BUSCLK 为 0 表示沿用 SYSCLK。";
        if (clocks.SysMhz > profile.MaximumSysClockMhz || clocks.BusMhz > profile.MaximumSysClockMhz)
            return $"{deviceId} 的系统/总线时钟不能超过已核实的 {profile.MaximumSysClockMhz} MHz。";
        if (clocks.SysMhz is not null && clocks.HseMhz is null)
            return "设置 SYSCLK 时必须填写与实际晶振一致的 HSECLK。";
        if (clocks.BusMhz is not null && clocks.SysMhz is null)
            return "设置 BUSCLK 时必须同时填写 HSECLK 和 SYSCLK；0 表示沿用明确配置的 SYSCLK。";
        if (clocks.BusMhz is > 0 && (clocks.SysMhz < clocks.BusMhz || clocks.SysMhz % clocks.BusMhz != 0))
            return "BUSCLK 必须是 SYSCLK 的整数分频，例如 160 / 80 MHz；160 / 100 MHz 不受支持。";
        return null;
    }

    public static void RequireValid(string deviceId, Ag32PinClockSettings clocks)
    {
        if (Validate(deviceId, clocks) is { } error) throw new StudioXException("AG32_PIN_PLAN_CLOCK", error);
    }

    public static Ag32ClockRecommendation[] Recommendations(string deviceId, Ag32PinClockSettings clocks, Ag32AnalogSettings analog)
    {
        if (!analog.Enabled || clocks.HseMhz != 8 || Ag32DeviceCatalog.Find(deviceId)?.CanMap != true) return [];
        // 仅列出已做原厂 analog_ip 实际布线回归的 8 MHz 外部输入组合，不推断其它晶振。
        const string evidence = "原厂 analog_ip 基础映射已做离线布局布线；当前引脚方案仍需重新编译。不会改变 HSECLK。";
        return [new("160 / 80 MHz · 系统 / 总线", new(8, 160, 80), evidence),
            new("100 / 50 MHz · 系统 / 总线", new(8, 100, 50), evidence)];
    }

    public static string Guidance(string deviceId, Ag32PinClockSettings clocks, Ag32AnalogSettings analog)
    {
        if (Validate(deviceId, clocks) is { } error) return error;
        if (!analog.Enabled) return "分频初检通过；PLL 与最终时序仍由转换器和布局布线核对。";
        if (clocks.HseMhz != 8) return "当前 HSE 没有预设验证记录；请按真实晶振配置并编译，不自动改为 8 MHz。";
        var bus = clocks.BusMhz is null or 0 ? clocks.SysMhz : clocks.BusMhz;
        return clocks.SysMhz > 160 || bus > 80
            ? "模拟 IP 当前频率高于离线推荐范围。200 / 100 MHz 回归存在负余量；允许保存，但必须通过实际时序检查才能下载。"
            : "分频初检通过；可使用下方离线推荐组合，最终仍以当前工程编译的时序结果为准。";
    }
}
