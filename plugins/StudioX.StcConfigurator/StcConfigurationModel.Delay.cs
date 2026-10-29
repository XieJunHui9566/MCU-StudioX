namespace StudioX.StcConfigurator;

using StudioX.Extensions.Abstractions;

public sealed partial class StcConfigurationModel
{
    private PluginMcuGeneratedFile[] RenderDelayFiles()
    {
        var frequency = ClockHz!.Value;
        // 沿用 STC-ISP 的两层倒数思路，但按配套 SDCC 的 volatile C 循环重新校准。
        // 默认/Os/O2：每次字节倒数及判断为 9 周期，每毫秒另有 21 周期；不是 Keil 的 DJNZ。
        var targetCycles = frequency / 1000m;
        var outer = 1;
        var inner = 1;
        var cycles = 39;
        for (var candidate = 1; candidate <= 256; candidate++)
        {
            var count = Math.Clamp((int)Math.Round((targetCycles - 21) / 9 - 256 * (candidate - 1) - candidate), 1, 256);
            var candidateCycles = 21 + 9 * (count + 256 * (candidate - 1) + candidate);
            if (Math.Abs(candidateCycles - targetCycles) < Math.Abs(cycles - targetCycles))
            {
                outer = candidate;
                inner = count;
                cycles = candidateCycles;
            }
        }
        var header = """
            #ifndef STUDIOX_GENERATED_DELAY_H
            #define STUDIOX_GENERATED_DELAY_H
            #include <stdint.h>
            /* 主循环 C 软件阻塞延时，单位 ms，范围 0..65535；0 直接返回。
             * 按生成时 SYSCLK 和配套 SDCC 默认/Os/O2 校准，不占用定时器，不关闭中断。
             * 中断、时钟误差、编译器及优化设置影响时间；O0 下会更慢。
             * 更改时钟后重新生成，不适用于精密脉冲；不自动喂狗，不要从 ISR 调用。 */
            void Delay_ms(uint16_t XMS);
            #endif
            """ + "\n";
        var source = "#include \"generated/mcuconfig/Delay.h\"\n" +
            "#if !defined(IAP15L2K61S2) || !defined(STUDIOX_STC15)\n#error Delay_target_mismatch\n#endif\n" +
            $"#if defined(STUDIOX_CLOCK_HZ) && STUDIOX_CLOCK_HZ != {frequency}UL\n#error Clock_changed_regenerate_Delay\n#endif\n" +
            $"#if defined(STUDIOX_CLOCK_DIVIDER) && STUDIOX_CLOCK_DIVIDER != {SystemDivider}\n#error Clock_divider_changed_regenerate_Delay\n#endif\n" +
            $"/* 源 {SourceClockHz} Hz / {SystemDivider} = SYSCLK {frequency} Hz。\n" +
            $" * SDCC 4.5.0 mcs51 默认/Os/O2：每轮约 {cycles} 个 SYSCLK 周期。\n" +
            " * 另有调用/返回开销，XMS 低字节借位时多 2 周期；极低 SYSCLK 下量化误差明显。\n" +
            " * 参考 STC-ISP STC15 的 C 倒数循环；volatile 保证等待不会被优化删除。\n" +
            " * 不使用汇编或编译器延时内建；更换工具链/优化等级后需重新核对时序。 */\n" +
            "void Delay_ms(uint16_t XMS)\n{\n" +
            "    volatile uint8_t i;\n    volatile uint8_t j;\n\n" +
            "    while (XMS != 0U)\n    {\n        --XMS;\n" +
            $"        i = {outer & 255}U;\n        j = {inner & 255}U;\n" +
            "        do\n        {\n            while (--j != 0U)\n            {\n            }\n        } while (--i != 0U);\n    }\n}\n";
        return [new("src/generated/mcuconfig/Delay.c", source), new("include/generated/mcuconfig/Delay.h", header)];
    }
}
