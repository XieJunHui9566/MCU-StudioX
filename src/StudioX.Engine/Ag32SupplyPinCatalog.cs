namespace StudioX.Engine;

using static Ag32FixedPinKind;

internal static class Ag32SupplyPinCatalog
{
    private static readonly Ag32FixedPin Vdd = new("VDD33", Supply, "3.3 V 电源");
    private static readonly Ag32FixedPin Vdda = new("VDDA", Supply, "模拟电源");
    private static readonly Ag32FixedPin Vdda33 = new("VDDA33", Supply, "3.3 V 模拟电源");
    private static readonly Ag32FixedPin Vbat = new("VBAT", Supply, "后备电源");
    private static readonly Ag32FixedPin Vbat33 = new("VBAT33", Supply, "后备电源");
    private static readonly Ag32FixedPin Vss = new("VSS33", Ground, "数字地（GND）");
    private static readonly Ag32FixedPin Vssa = new("VSSA", Ground, "模拟地（GNDA）");
    private static readonly Ag32FixedPin Gnd = new("GND", Ground, "地");
    private static readonly Ag32FixedPin Vref = new("VREFP", Reference, "模拟正参考电压");

    private static readonly Ag32FixedPin OscIn = new("OSC_IN", Dedicated, "外部高速晶振输入");
    private static readonly Ag32FixedPin OscOut = new("OSC_OUT", Dedicated, "外部高速晶振输出");
    private static readonly Ag32FixedPin Osc32In = new("OSC32_IN", Dedicated, "外部低速晶振输入");
    private static readonly Ag32FixedPin Osc32Out = new("OSC32_OUT", Dedicated, "外部低速晶振输出");
    private static readonly Ag32FixedPin Reset = new("NRST", Dedicated, "外部复位，低电平有效");
    private static readonly Ag32FixedPin Boot = new("BOOT0", Dedicated, "启动模式选择");
    private static readonly Ag32FixedPin Rwds = new("RWDS", Dedicated, "封装内 HyperRAM 数据选通");
    private static readonly Ag32FixedPin Nc = new("NC", Dedicated, "未连接");

    // AG32 MCU Reference Manual，2026-06-01，第 23–29 页。VH100 另见 AG32VH 系列应用指南第 6 页。
    // VH 的 HyperRAM 改变了部分脚位的电气连接，必须按已核实的目标选表。
    internal static IReadOnlyDictionary<int, Ag32FixedPin> ForTarget(string target) => target switch
    {
        "AGRV2KQ32" => new Dictionary<int, Ag32FixedPin> { [4] = Reset, [30] = Boot, [6] = Vdda33, [16] = Vdd, [17] = Gnd, [32] = Vdd },
        "AGRV2KL48" => new Dictionary<int, Ag32FixedPin>
        {
            [3] = Osc32In,
            [4] = Osc32Out,
            [5] = OscIn,
            [6] = OscOut,
            [7] = Reset,
            [44] = Boot,
            [1] = Vbat,
            [8] = Vssa,
            [9] = Vdda,
            [23] = Vss,
            [24] = Vdd,
            [36] = Vdd,
            [47] = Vss,
            [48] = Vdd
        },
        "AGRV2KL64" => new Dictionary<int, Ag32FixedPin>
        {
            [3] = Osc32In,
            [4] = Osc32Out,
            [5] = OscIn,
            [6] = OscOut,
            [7] = Reset,
            [60] = Boot,
            [1] = Vbat,
            [12] = Vssa,
            [13] = Vdda,
            [18] = Vss,
            [19] = Vdd,
            [32] = Vdd,
            [48] = Vdd,
            [63] = Vss,
            [64] = Vdd
        },
        "AGRV2KL64H" => new Dictionary<int, Ag32FixedPin>
        {
            [3] = Osc32In,
            [4] = Osc32Out,
            [5] = OscIn,
            [6] = OscOut,
            [7] = Reset,
            [60] = Boot,
            [8] = Rwds,
            [22] = Rwds,
            [1] = Vbat,
            [12] = Gnd,
            [13] = Vdd,
            [18] = Gnd,
            [19] = Vdd,
            [30] = Vdd,
            [31] = Gnd,
            [32] = Vdd,
            [48] = Vdd,
            [63] = Gnd,
            [64] = Vdd
        },
        "AGRV2KL100" => new Dictionary<int, Ag32FixedPin>
        {
            [8] = Osc32In,
            [9] = Osc32Out,
            [12] = OscIn,
            [13] = OscOut,
            [14] = Reset,
            [94] = Boot,
            [19] = Nc,
            [49] = Nc,
            [6] = Vbat,
            [10] = Vss,
            [11] = Vdd,
            [20] = Vssa,
            [21] = Vref,
            [22] = Vdda,
            [27] = Vss,
            [28] = Vdd,
            [50] = Vdd,
            [74] = Vss,
            [75] = Vdd,
            [99] = Vss,
            [100] = Vdd
        },
        "AGRV2KL100H" => new Dictionary<int, Ag32FixedPin>
        {
            [8] = Osc32In,
            [9] = Osc32Out,
            [12] = OscIn,
            [13] = OscOut,
            [14] = Reset,
            [94] = Boot,
            [15] = Rwds,
            [35] = Rwds,
            [19] = Nc,
            [29] = Nc,
            [31] = Nc,
            [33] = Nc,
            [39] = Nc,
            [41] = Nc,
            [43] = Nc,
            [45] = Nc,
            [6] = Vbat33,
            [10] = Gnd,
            [11] = Vdd,
            [20] = Gnd,
            [21] = Vref,
            [22] = Vdda,
            [27] = Gnd,
            [28] = Vdd,
            [46] = Vdd,
            [47] = Gnd,
            [48] = Vdd,
            [49] = Gnd,
            [50] = Vdd,
            [74] = Gnd,
            [75] = Vdd,
            [99] = Gnd,
            [100] = Vdd
        },
        _ => new Dictionary<int, Ag32FixedPin>()
    };
}
