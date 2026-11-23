namespace StudioX.Engine;

using System.Text;

internal static class Ag32GpioInterrupts
{
    internal static void Generate(IEnumerable<Ag32PinAssignment> assignments, StringBuilder header, StringBuilder source, StringBuilder init)
    {
        var groups = assignments.Where(pin => pin.Interrupt != "NONE").GroupBy(pin => pin.Function.Split('_')[0]).ToArray();
        foreach (var group in groups)
        {
            var port = group.Key;
            var priority = Ag32InterruptCode.SharedPriority(port, group.Select(p => p.InterruptPriority));
            Ag32InterruptCode.Callback(header, source, $"void SX_{port}_Interrupt(uint8_t pins)",
                $"{port} 引脚中断。pins 的每个置位代表一个触发引脚。", "(void)pins;");
            var mask = string.Join(" | ", group.Select(pin => "GPIO_BIT" + pin.Function[^1]));
            source.AppendLine($"__attribute__((weak)) void {port}_isr(void)").AppendLine("{")
                .AppendLine($"    const uint8_t pending = GPIO_GetMaskedIntStatus({port}) & ({mask});");
            foreach (var pin in group)
            {
                var bit = "GPIO_BIT" + pin.Function[^1];
                var counter = $"StudioX_{pin.Function}_InterruptCount";
                header.AppendLine($"extern volatile uint32_t {counter};");
                init.AppendLine($"    GPIO_DisableInt({port}, {bit});")
                    .AppendLine($"    GPIO_IntConfig({port}, {bit}, GPIO_INTMODE_{pin.Interrupt});")
                    .AppendLine($"    GPIO_ClearInt({port}, {bit});");
                source.AppendLine($"    if (pending & {bit})").AppendLine("    {");
                if (pin.Interrupt is "LOWLEVEL" or "HIGHLEVEL")

                {
                    source.AppendLine("        /* 电平解除后，可用 GPIO_EnableInt 重新使能此引脚。 */")
                                            .AppendLine($"        GPIO_DisableInt({port}, {bit});");
                }
                source.AppendLine($"        GPIO_ClearInt({port}, {bit});")
                    .AppendLine($"        ++{counter};").AppendLine("    }");
            }
            source.AppendLine($"    if (pending) SX_{port}_Interrupt(pending);").AppendLine("}");
            foreach (var pin in group)
            {
                source.AppendLine($"volatile uint32_t StudioX_{pin.Function}_InterruptCount;");
            }
            init.AppendLine($"    GPIO_EnableInt({port}, {mask});")
                .AppendLine($"    INT_EnableIRQ({port}_IRQn, {priority}u);");
        }
        if (groups.Length != 0)

        {
            init.AppendLine("    INT_EnableIntExternal();").AppendLine("#ifndef STUDIOX_FREERTOS")
                            .AppendLine("    INT_EnableIntGlobal();").AppendLine("#endif");
        }
    }
}
