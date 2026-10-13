namespace StudioX.Application.StcTiming;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed partial class StcTimingService
{
    public StcTimingPreview GenerateDelay(StcTimingContext context, StcTimingSettings settings, string functionName)
    {
        var clock = Clock(context, settings, functionName);
        var ticks = checked((long)decimal.Round((decimal)clock * settings.DelayMicroseconds / (settings.TimerDivider * 1000000m), 0, MidpointRounding.AwayFromZero));
        if (ticks < 1)
        {
            throw new StudioXException("STC_TIMING_RESOLUTION", "延时短于一个计时节拍，请增加延时或选择更快的定时器时钟。");
        }
        var actual = ticks * settings.TimerDivider * 1000000m / clock;
        var error = (actual - settings.DelayMicroseconds) * 100m / settings.DelayMicroseconds;
        var timer = settings.DelayTimer;
        var mask = timer == 0 ? 0x0f : 0xf0;
        var clockBit = timer == 0 ? 0x80 : 0x40;
        var modern = context.Profile.Family != "stc89";
        var fullBlocks = ticks / 65536;
        var remainder = (int)(ticks % 65536);
        var reload = remainder == 0 ? 0 : 65536 - remainder;
        var setup = modern ? "unsigned char saved_auxr;" : "";
        var saveClock = modern ? $"saved_auxr = AUXR & 0x{clockBit:X2};" : "";
        var configure = modern ? $"AUXR = (AUXR & 0x{255 - clockBit:X2}) | 0x{(settings.TimerDivider == 1 ? clockBit : 0):X2};" :
            $"/* STC89 的 {settings.TimerDivider}T 须与 ISP 硬件选项一致；此函数不切换 CPU 模式。 */";
        var restore = modern ? $"AUXR = (AUXR & 0x{255 - clockBit:X2}) | saved_auxr;" : "";
        var loops = fullBlocks > 0 ? $"unsigned int blocks = {fullBlocks}U;" : "";
        var waits = (fullBlocks > 0 ? $"while (blocks-- != 0U)\n    {{\n        {functionName}_wait(0U);\n    }}\n    " : "") +
            (remainder > 0 ? $"{functionName}_wait(0x{reload:X4}U);" : "");
        var outputBusy = context.Profile.ClockOutputRegister is { } clockOutput ? $" || ({clockOutput} & 0x{1 << timer:X2})" : "";
        var code = Guard(context, clock) + $$"""
            /* 固定延时 {{settings.DelayMicroseconds}} us：独占 Timer {{timer}}，16 位模式 1，{{settings.TimerDivider}}T。
             * 名义计数时间 {{Number(actual)}} us；不含调用、轮询、重装载和中断执行耗时。
             * 仅主循环调用，不可重入；其它代码/ISR 不得在等待中重配本定时器。
             * 成功返回 1；定时器运行中或中断/时钟输出已启用则返回 0，不修改其配置。
             */
            static void {{functionName}}_wait(unsigned int reload)
            {
                TF{{timer}} = 0;
                TH{{timer}} = (unsigned char)(reload >> 8);
                TL{{timer}} = (unsigned char)reload;
                TR{{timer}} = 1;
                while (TF{{timer}} == 0) { }
                TR{{timer}} = 0;
            }

            unsigned char {{functionName}}(void)
            {
                unsigned char saved_ea = EA;
                unsigned char saved_mode;
                unsigned char saved_high;
                unsigned char saved_low;
                unsigned char saved_flag;
                {{setup}}
                {{loops}}
                EA = 0;
                /* 模式 3 会共享另一计时器的控制位，不允许借用。 */
                if (TR{{timer}} || ET{{timer}} || (TMOD & 0x03) == 0x03 || (TMOD & 0x30) == 0x30{{outputBusy}})
                {
                    EA = saved_ea;
                    return 0;
                }
                saved_mode = TMOD & 0x{{mask:X2}};
                saved_high = TH{{timer}};
                saved_low = TL{{timer}};
                saved_flag = TF{{timer}};
                {{saveClock}}
                TMOD = (TMOD & 0x{{255 - mask:X2}}) | 0x{{(timer == 0 ? 1 : 16):X2}};
                {{configure}}
                EA = saved_ea;
                {{waits}}
                EA = 0;
                TR{{timer}} = 0;
                TH{{timer}} = saved_high;
                TL{{timer}} = saved_low;
                TF{{timer}} = saved_flag;
                TMOD = (TMOD & 0x{{255 - mask:X2}}) | saved_mode;
                {{restore}}
                EA = saved_ea;
                return 1;
            }
            """ + "\n";
        return new(functionName, code,
            $"Timer {timer} · {settings.TimerDivider}T · {ticks:N0} 个计数 · {fullBlocks} 个满周期 + {remainder} 个计数\n名义延时 {Number(actual)} µs；量化误差 {Number(error)}%。",
            ["Timer " + timer], ticks, reload, actual, error, true,
            "实际阻塞时间还包括调用、轮询、分段重装载及中断耗时；不用于精确短脉冲。定时器运行中或中断/时钟输出已启用时返回 0，请检查返回值。");
    }

    public StcTimingPreview GenerateUart(StcTimingContext context, StcTimingSettings settings, string functionName)
    {
        var clock = Clock(context, settings, functionName);
        var timer2 = settings.BaudGenerator == "timer2";
        var maximum = timer2 ? 65536 : 256;
        var choices = timer2 ? new[] { 0 } : new[] { 0, 1 };
        // 分母相邻的两个整数都比较实际误差，避免简单四舍五入选到更差的速率。
        var candidates = choices.SelectMany(smod =>
        {
            var factor = timer2 ? 4 : 32 / (1 << smod);
            var ideal = (decimal)clock / (factor * settings.TimerDivider * (decimal)settings.BaudRate);
            return new[] { (long)decimal.Floor(ideal), (long)decimal.Ceiling(ideal) }.Distinct()
                .Where(n => n is >= 1 && n <= maximum)
                .Select(n => (Ticks: n, Smod: smod, Rate: (decimal)clock / (factor * settings.TimerDivider * n)));
        }).OrderBy(c => decimal.Abs(c.Rate - settings.BaudRate)).ThenBy(c => c.Smod).ToArray();
        if (candidates.Length == 0)
        {
            throw new StudioXException("STC_TIMING_BAUD_RANGE", "该时钟与计时分频无法在所选发生器的计数范围内产生目标波特率。");
        }
        var best = candidates[0];
        var error = (best.Rate - settings.BaudRate) * 100m / settings.BaudRate;
        var reload = maximum - (int)best.Ticks;
        var conflict = context.Mon51 && settings.UartPins == "default";
        var safe = decimal.Abs(error) <= 2m && !conflict;
        var pins = settings.UartPins switch
        {
            "p36" => "P3.6 / P3.7",
            "p16" => "P1.6 / P1.7",
            _ => "P3.0 / P3.1"
        };
        var pinCode = context.Profile.HasPinSwitch ? $"P_SW1 = (P_SW1 & 0x3F) | 0x{(settings.UartPins == "p36" ? 64 : settings.UartPins == "p16" ? 128 : 0):X2};" :
            "/* 本型号只生成默认 UART1 引脚配置。 */";
        var modern = context.Profile.Family != "stc89";
        if (modern)
        {
            var port = settings.UartPins == "p16" ? 1 : 3;
            var rx = settings.UartPins == "default" ? 1 : 64;
            var tx = settings.UartPins == "default" ? 2 : 128;
            pinCode += $"\n    /* RX 为高阻输入，TX 为推挽输出；仅修改所选引脚。 */\n    P{port}M1 = (P{port}M1 & 0x{255 - tx:X2}) | 0x{rx:X2};\n    P{port}M0 = (P{port}M0 & 0x{255 - rx:X2}) | 0x{tx:X2};";
        }
        var configuration = timer2 ? $$"""
                /* Timer 2 独占：内部时钟、停止后装载；不启用中断或时钟输出。 */
                AUXR &= 0xEF;
                AUXR = (AUXR & 0xF3) | 0x{{(settings.TimerDivider == 1 ? 4 : 0):X2}};
                T2L = 0x{{reload & 255:X2}};
                T2H = 0x{{reload >> 8:X2}};
                AUXR |= 0x11;
            """ : $$"""
                TR1 = 0;
                TMOD = (TMOD & 0x0F) | 0x20;
                {{(modern ? $"AUXR = (AUXR & 0xBE) | 0x{(settings.TimerDivider == 1 ? 64 : 0):X2};" : $"/* {settings.TimerDivider}T 须与 STC89 ISP 选项一致。 */")}}
                {{(modern ? "" : "T2CON &= 0xCF; /* UART1 收发均选择 Timer 1，不停用 Timer 2。 */")}}
                PCON = (PCON & 0x7F) | 0x{{(best.Smod == 1 ? 128 : 0):X2}};
                TH1 = 0x{{reload:X2}};
                TL1 = TH1;
                TF1 = 0;
                TR1 = 1;
            """;
        var busy = timer2 ? "(AUXR & 0x10) || (IE2 & 0x04)" : "TR1 || ET1 || (TMOD & 0x03) == 0x03";
        if (context.Profile.ClockOutputRegister is { } uartClockOutput)
        {
            busy += $" || ({uartClockOutput} & 0x{(timer2 ? 4 : 2):X2})";
        }
        var monitorGuard = settings.UartPins == "default" ? "\n#if defined(STUDIOX_MON51_DEBUG)\n#error UART1_default_pins_conflict_with_Mon51\n#endif\n" : "";
        var code = Guard(context, clock) + monitorGuard + $$"""
            /* UART1，8N1：目标 {{settings.BaudRate}} baud，名义 {{Number(best.Rate)}} baud，误差 {{Number(error)}}%。
             * 独占 {{(timer2 ? "Timer 2" : "Timer 1")}} 与 UART1；RX/TX：{{pins}}，请核对封装与板级连线。
             * 此函数只初始化，不开启串口/全局中断，不发送数据；初始化前须结束原串口传输。
             * 成功返回 1；发生器运行中或中断/时钟输出已启用则返回 0，不修改配置。
             */
            unsigned char {{functionName}}(void)
            {
                unsigned char saved_ea = EA;
                EA = 0;
                if (ES || {{busy}})
                {
                    EA = saved_ea;
                    return 0;
                }
                {{pinCode}}
                SCON = 0x50;
                {{configuration}}
                EA = saved_ea;
                return 1;
            }
            """ + "\n";
        var notice = conflict ? "Mon51 占用 P3.0/P3.1，请切换用户 UART1 引脚后生成。" :
            decimal.Abs(error) > 2m ? "名义波特率误差超过 2%，已阻止插入；请调整时钟、分频或发生器。" :
            "误差仅包括重装值量化，不包含 RC/晶振误差；实际连线、引脚电气模式和其它串口/定时器占用需核对。";
        return new(functionName, code,
            $"UART1 · {(timer2 ? "Timer 2 / 16 位" : "Timer 1 / 8 位自动重装")} · {settings.TimerDivider}T · 重装 0x{reload:X4}\n实际 {Number(best.Rate)} baud；误差 {Number(error)}%；{pins}" + (timer2 ? "" : $"；SMOD={best.Smod}"),
            ["UART1", timer2 ? "Timer 2" : "Timer 1", pins], best.Ticks, reload, best.Rate, error, safe, notice);
    }

    private static int Clock(StcTimingContext context, StcTimingSettings settings, string functionName)
    {
        context.Profile.Validate(settings);
        if (!Regex.IsMatch(functionName, @"^sx_[A-Za-z][A-Za-z0-9_]{0,35}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            throw new StudioXException("STC_TIMING_NAME", "函数名须以 sx_ 加字母开头，只包含 ASCII 字母、数字或下划线，最长 39 字符。");
        }
        var clock = context.ProjectClockHz ?? settings.ReferenceClockHz
            ?? throw new StudioXException("STC_TIMING_CLOCK", "工程未指定时钟，请明确填写实际系统时钟（分频后），不使用猜测的默认频率。");
        if (context.Profile.Family == "stc89" && settings.TimerDivider == 6 && clock > 24000000)
        {
            throw new StudioXException("STC_TIMING_CLOCK", "STC89 的 6T 配置需 ≤24 MHz。");
        }
        return clock;
    }

    private static string Guard(StcTimingContext context, int clock) => $$"""
        #include "{{context.Profile.Header}}"

        /* 由 MCU StudioX 按工程预设生成：{{context.Project.DeviceId}}，系统时钟 {{clock}} Hz。
         * 不修改时钟源；若程序改变系统分频，须按分频后的系统时钟重新生成。
         */
        #if !defined({{context.Project.DeviceId}}) || !defined(STUDIOX_{{context.Profile.Family.ToUpperInvariant()}})
        #error Generated_timing_device_does_not_match_project
        #endif
        #if defined(STUDIOX_CLOCK_HZ) && STUDIOX_CLOCK_HZ != {{clock}}UL
        #error Project_clock_changed_regenerate_timing_functions
        #endif

        """;

    private static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
