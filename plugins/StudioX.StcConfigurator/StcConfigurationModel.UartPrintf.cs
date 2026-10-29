namespace StudioX.StcConfigurator;

using System.Text;

public sealed partial class StcConfigurationModel
{
    private bool PrintfEnabled(int index) => Peripheral("UART" + index)?["parameters"]?["printfEnabled"]?.GetValue<bool>() == true;

    private (string Header, string Source) RenderUartPrintf()
    {
        var enabled = Enumerable.Range(1, 2).Where(PrintfEnabled).ToArray();
        if (enabled.Length == 0)
        {
            return ("", "");
        }
        var header = new StringBuilder("""
            /* printf(format, ...) 调用方式；成功返回字符数，失败返回 -1，可能已输出部分内容。
             * 先调用 sx_mcuconfig_init，仅供主循环使用；禁止 ISR、嵌套或并发格式化。
             * 轮询期间 UART 中断须关闭；不要由其它代码同时读写 TX 寄存器/标志。
             * 与 SDCC 4.5.0 printf 使用相同格式化库：整数、字符串、宽度/精度等；
             * 当前 mcs51 库未启用浮点格式，%f 会输出 <NO FLOAT>，也不支持 %e/%g。
             * 原样输出换行，需要 CRLF 时请使用 \r\n；不使用定长输出缓冲区。
             */

            """);
        var source = new StringBuilder("""

            #include <stdarg.h>
            #include <stdio.h>
            /* 使用工具链 printf 的流式格式化入口，不接管全局 putchar，也不分配大缓冲区。
             * 默认 SDCC 格式化库不可重入；两个 UART 共用锁，锁内仍允许其它中断运行。
             * 应用也不得从 ISR 调用其它 printf/sprintf 家族函数。
             */
            static volatile unsigned char sx_printf_busy;
            static unsigned char sx_printf_acquire(void) __reentrant
            {
                unsigned char sx_saved_ea = EA;
                EA = 0;
                if (sx_printf_busy)
                {
                    EA = sx_saved_ea;
                    return 0;
                }
                sx_printf_busy = 1;
                EA = sx_saved_ea;
                return 1;
            }

            """);
        foreach (var index in enabled)
        {
            var prefix = "sx_usart" + index;
            var timer = Parameter("UART" + index, "clockSource");
            var clockStopped = timer == "T1" ? "!TR1" : "!(AUXR & 0x10)";
            var unavailable = index == 1
                ? "ES || (SCON & 0xC0) != 0x40 || " + (timer == "T1" ? "(AUXR & 0x01)" : "!(AUXR & 0x01)")
                : "(IE2 & 0x01) || (S2CON & 0x80)";
            var complete = index == 1 ? "TI" : "(S2CON & 0x02)";
            var clear = index == 1 ? "TI = 0;" : "S2CON &= 0xFD;";
            var buffer = index == 1 ? "SBUF" : "S2BUF";
            var uart = Peripheral("UART" + index)!;
            // 以实际量化波特率留出至少 16 个位时间的指令次数；这是轮询次数上限，不承诺毫秒。
            var polls = (long)decimal.Ceiling((ClockHz ?? 0) / ExtendedBaud(uart, ClockHz ?? 0).Rate * 16m) + 64;
            header.Append("int USART").Append(index).Append("_printf(const char *sx_format, ...);\n");
            source.Append($$"""
                static unsigned char {{prefix}}_pending;
                static unsigned char {{prefix}}_wait(void)
                {
                    unsigned long sx_remaining = {{polls}}UL;
                    while (!{{complete}})
                    {
                        if (--sx_remaining == 0)
                        {
                            return 0;
                        }
                    }
                    {{clear}}
                    {{prefix}}_pending = 0;
                    return 1;
                }

                static void {{prefix}}_putchar(char sx_value, void *sx_context) __reentrant
                {
                    unsigned char *sx_failed = (unsigned char *)sx_context;
                    if (*sx_failed)
                    {
                        return;
                    }
                    /* 上次超时的字节可能仍在发送，重试也必须先等完成，不能覆盖 SBUF。 */
                    if ({{prefix}}_pending && !{{prefix}}_wait())
                    {
                        *sx_failed = 1;
                        return;
                    }
                    {{clear}}
                    {{buffer}} = sx_value;
                    {{prefix}}_pending = 1;
                    if (!{{prefix}}_wait())
                    {
                        *sx_failed = 1;
                    }
                }

                int USART{{index}}_printf(const char *sx_format, ...)
                {
                    va_list sx_args;
                    int sx_count;
                    unsigned char sx_failed = 0;
                    if (!sx_format || {{unavailable}} || {{clockStopped}} || !sx_printf_acquire())
                    {
                        return -1;
                    }
                    va_start(sx_args, sx_format);
                    sx_count = _print_format({{prefix}}_putchar, &sx_failed, sx_format, sx_args);
                    va_end(sx_args);
                    sx_printf_busy = 0;
                    return sx_failed ? -1 : sx_count;
                }

                """);
        }
        return (header.ToString(), source.ToString());
    }
}
