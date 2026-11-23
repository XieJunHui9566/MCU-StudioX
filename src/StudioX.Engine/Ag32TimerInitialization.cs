namespace StudioX.Engine;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public static partial class Ag32TimerInitialization
{
    private const string Marker = "#@StudioX:TIMER ";
    private static readonly Regex Lines = new(@"(?<![^\r\n])[ \t]*#@StudioX:TIMER\b[^\r\n]*(?:\r\n|\n|\r|\z)");
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static bool IsBasic(string? module) => module is "TIMER0.1" or "TIMER0.2" or "TIMER1.1" or "TIMER1.2";
    public static bool IsGeneral(string? module) => module is "GPTIMER0" or "GPTIMER1" or "GPTIMER2" or "GPTIMER3" or "GPTIMER4";
    private static StudioXException Error(string message) => new("AG32_TIMER_INIT", message);

    private static void Validate(Ag32TimerSettings s)
    {
        var channels = s.Channels;
        if ((!IsGeneral(s.Module) && !IsBasic(s.Module)) || s.FrequencyHz <= 0 ||
            s.InterruptPriority is < 1 or > 15 || s.BreakInterrupt && s.BreakPolarity is null ||
            s.Encoder is not (null or "X2_TI0" or "X2_TI1" or "X4_TI01") || s.EncoderTop == 0 ||
            s.EncoderFilter is < 0 or > 15 || s.DeadTimeNs < 0 || s.BreakPolarity is not (null or "HIGH" or "LOW"))

        {
            throw Error($"{s.Module} 定时器参数无效。");
        }
        var output = channels.Any(c => c?.IsOutput == true);
        if ((IsBasic(s.Module) && (s.Encoder is not null || channels.Any(c => c is not null))) ||
            (s.DeadTimeNs > 0 && !channels.Any(c => c?.Complementary == true)) ||
            (s.Encoder is not null && channels.Any(c => c is not null)) || (!output && (s.DeadTimeNs != 0 || s.BreakPolarity is not null)))

        {
            throw Error($"{s.Module} 的计数模式与通道配置冲突。");
        }
        foreach (var c in channels.OfType<Ag32TimerChannel>())
        {
            bool validOutput = c.CompareMode is null
                ? c.Duty is >= 0 and <= 100 && c.CompareTicks is null
                : c.CompareMode is "TOGGLE" or "ACTIVE" or "INACTIVE" && c.CompareTicks.HasValue && c.Duty is null;
            bool valid = c.IsOutput
                ? validOutput && c.Edge is null && c.Filter == 0 && c.Divider == 1
                : c.Edge is "RISING" or "FALLING" or "BOTHEDGE" && !c.Complementary && c.CompareTicks is null &&
                  c.Filter is >= 0 and <= 15 && c.Divider is 1 or 2 or 4 or 8;
            if (!valid)
            {
                throw Error($"{s.Module} 的输出比较、PWM 或输入捕获参数无效。");
            }
        }
    }

    internal static Ag32TimerSettings[] Read(byte[] source)
    {
        var settings = new List<Ag32TimerSettings>();
        foreach (Match match in Lines.Matches(Encoding.UTF8.GetString(source).TrimStart('\uFEFF')))
        {
            var line = match.Value.Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal))
            {
                throw Error("定时器配置缺少参数。");
            }
            Ag32TimerSettings s;
            try
            {
                s = JsonSerializer.Deserialize<Ag32TimerSettings>(line[Marker.Length..], Json) ?? throw Error("定时器配置为空。");
            }
            catch (JsonException ex)
            {
                throw Error($"无法读取定时器配置：{ex.Message}");
            }
            Validate(s);
            if (settings.Any(item => item.Module == s.Module))
            {
                throw Error($"{s.Module} 定时器配置重复。");
            }
            settings.Add(s);
        }
        return settings.OrderBy(s => s.Module, StringComparer.Ordinal).ToArray();
    }

    internal static byte[] Write(byte[] source, IReadOnlyList<Ag32TimerSettings>? settings)
    {
        if (settings is null)
        {
            return source;
        }
        var ordered = settings.OrderBy(s => s.Module, StringComparer.Ordinal).ToArray();
        if (Read(source).SequenceEqual(ordered))
        {
            return source;
        }
        if (ordered.DistinctBy(s => s.Module).Count() != ordered.Length)
        {
            throw Error("定时器配置重复。");
        }
        var offset = source is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        var text = Encoding.UTF8.GetString(source.AsSpan(offset));
        var newline = Regex.Match(text, @"\r\n|\r|\n") is { Success: true } m ? m.Value : "\n";
        var result = new StringBuilder(Lines.Replace(text, ""));
        foreach (var s in ordered)
        {
            Validate(s);
            if (result.Length > 0 && result[^1] is not ('\n' or '\r'))
            {
                result.Append(newline);
            }
            result.Append(Marker).Append(JsonSerializer.Serialize(s, Json)).Append(newline);
        }
        return [.. source.AsSpan(0, offset), .. Encoding.UTF8.GetBytes(result.ToString())];
    }

    internal static bool IsInternalOnly(byte[] source)
    {
        var settings = Read(source);
        return settings.Length > 0 && settings.All(s => s.Encoder is null && s.Channels.All(c => c is null));
    }

    private static (int Divider, long Period) TimeBase(Ag32TimerSettings s, uint clock, bool center, bool narrow)
    {
        var basic = IsBasic(s.Module);
        long maxPeriod = basic ? (narrow ? ushort.MaxValue : uint.MaxValue) : center ? uint.MaxValue : 1L << 32;
        int maxDivider = basic ? 256 : 65536;
        if (clock == 0 || s.FrequencyHz > clock / (basic ? 1m : 2m) || s.FrequencyHz < clock / ((decimal)maxPeriod * maxDivider * (center ? 2 : 1)))

        {
            throw Error($"{s.Module} 的频率超出当前总线时钟 {clock} Hz 下的计数范围。");
        }
        var ticks = clock / s.FrequencyHz / (center ? 2 : 1);
        int divider = Math.Clamp((int)decimal.Ceiling(ticks / maxPeriod), 1, maxDivider);
        if (basic)
        {
            divider = divider <= 1 ? 1 : divider <= 16 ? 16 : 256;
        }
        return (divider, Math.Min(maxPeriod, (long)decimal.Round(ticks / divider, 0, MidpointRounding.AwayFromZero)));
    }

    public static string InitCode(Ag32TimerSettings s, IReadOnlyList<Ag32PinAssignment> pins, uint clock, out string timing, Ag32Setup? setup = null, bool deferStart = false)
    {
        Validate(s);
        var basic = IsBasic(s.Module);
        var module = basic ? s.Module[..6] : s.Module;
        setup ??= new Ag32Setup();
        var advanced = setup.Units.ContainsKey(s.Module);
        var dir = basic || !advanced ? "向上" : setup.Value(module, "Direction");
        bool center = dir == "中心对齐";
        var narrow = basic && setup.Value(s.Module, "Width") == "16";
        var free = basic && setup.Value(s.Module, "Mode") == "自由运行";
        var (divider, period) = free ? (setup.Number(s.Module, "FreeDiv"), narrow ? 65536L : 1L << 32) :
            s.Encoder is null ? TimeBase(s, clock, center, narrow) : (1, 1L + s.EncoderTop);
        var details = new List<string>();
        var code = new StringBuilder();
        if (s.Encoder is null)
        {
            var hz = clock / (decimal)divider / period / (center ? 2 : 1);
            details.Add(FormattableString.Invariant($"实际频率 {hz:G10} Hz · 计数时钟 {clock / (decimal)divider:G10} Hz · 每周期 {period * (center ? 2 : 1)} 计数"));
            var callback = !s.UpdateInterrupt ? "" : basic ? $"到期时调用 SX_{module}_PeriodElapsed({s.Module[^1]}u)。"
                : $"到期时调用 SX_{module}_PeriodElapsed()。";
            code.AppendLine(FormattableString.Invariant($"/* {s.Module} 周期计数，频率约 {hz:G10} Hz，周期约 {1000m / hz:G10} ms。{callback} */"));
        }
        else
        {
            code.AppendLine($"/* {module} 按正交编码器 A、B 相计数，范围为 0 至 {s.EncoderTop}。 */");
        }
        string Pin(string signal, bool input = false)
        {
            var pin = pins.FirstOrDefault(p => p.Function == $"{module}_{signal}") ?? throw Error($"{module} 请为 {signal} 分配引脚。");
            if (input && pin.Direction == "OUTPUT" || !input && pin.Direction == "INPUT")

            {
                throw Error($"{module}_{signal} 的 VE 引脚方向与{(input ? "输入" : "输出")}模式冲突，请重新分配引脚。");
            }
            return $"PIN_{pin.PinNumber}";
        }
        void Emit(string line) => code.Append("    ").AppendLine(line);
        code.AppendLine($"if (StudioX_PeripheralClockHz() == {clock}u)").AppendLine("{");
        if (basic)
        {
            var n = s.Module[^1];
            Emit($"TIM_DisableTimer{n}({module});");
            Emit($"TIM_ClearInt{n}({module});");
            Emit($"TIM_SetLoad{n}({module}, {(free ? period - 1 : period)}u);");
            if (s.UpdateInterrupt)
            {
                code.AppendLine(Ag32InterruptCode.Enable(module, s.InterruptPriority, "    "));
            }
            var interrupt = s.UpdateInterrupt ? " | TIMER_CTRL_INT_EN" : "";
            Emit($"{module}->Timer{n}Ctrl = TIMER_MODE_{(free ? "FREE_RUNNING" : setup.Value(s.Module,"Mode") == "单次" ? "ONE_SHOT" : "PERIODIC")} | TIMER_SIZE_{(narrow ? 16 : 32)} | TIMER_PRESCALE_{divider}{interrupt} | TIMER_CTRL_ENABLE;");
        }
        else
        {
            Emit($"SYS_APBReset(APB_MASK_{module});");
            Emit("GPTIMER_InitTypeDef base;");
            Emit("GPTIMER_StructInit(&base);");
            Emit($"base.Prescaler = {divider - 1}u;");
            Emit($"base.Autoreload = {(center ? period : period - 1)}u;");
            if (advanced)
            {
                Emit($"base.CounterMode = GPTIMER_COUNTERMODE_{(center ? "CENTER_BOTH" : dir == "向下" ? "DOWN" : "UP")};");
                Emit($"base.RepetitionCounter = {setup.Number(module,"Repeat")}u;");
            }
            Emit($"GPTIMER_Init({module}, &base);");
            if (s.Encoder is not null)
            {
                details.Add($"正交编码器 · A / CH0 → {Pin("CH0", true)} · B / CH1 → {Pin("CH1", true)} · 计数范围 0–{s.EncoderTop}");
                Emit("GPTIMER_ENCODER_InitTypeDef encoder;");
                Emit("GPTIMER_ENCODER_StructInit(&encoder);");
                Emit($"encoder.EncoderMode = GPTIMER_ENCODERMODE_{s.Encoder};");
                Emit($"encoder.IC0Filter = encoder.IC1Filter = {CaptureFilter(s.EncoderFilter)};");
                Emit($"GPTIMER_ENCODER_Init({module}, &encoder);");
            }
            var channels = s.Channels;
            var output = channels.Any(c => c?.IsOutput == true);
            if (output)
            {
                Emit("GPTIMER_OC_InitTypeDef oc;");
                Emit("GPTIMER_OC_StructInit(&oc);");
                Emit("oc.OCState = GPTIMER_OCSTATE_ENABLE;");
            }
            if (channels.Any(c => c?.Edge is not null))
            {
                Emit("GPTIMER_IC_InitTypeDef ic;");
                Emit("GPTIMER_IC_StructInit(&ic);");
            }
            for (int ch = 0; ch < channels.Length; ch++)
            {
                if (channels[ch] is not { } c)
                {
                    continue;
                }
                var route = $"CH{ch} → {Pin($"CH{ch}", c.Edge is not null)}";
                if (c.IsOutput)
                {
                    if (c.Complementary)
                    {
                        route += $" · CHN{ch} → {Pin($"CHN{ch}")}";
                    }
                    string mode;
                    long compare;
                    string description;
                    if (c.Duty is decimal duty)
                    {
                        var ticks = (long)decimal.Round(period * duty / 100m, 0, MidpointRounding.AwayFromZero);
                        var delayed = advanced && setup.Value(module, "Pulse") == "单脉冲";
                        mode = ticks == 0 ? "FORCED_INACTIVE" : ticks == period ? "FORCED_ACTIVE" : delayed ? "PWM2" : "PWM1";
                        compare = ticks == period || ticks == 0 ? 0 : dir == "向下" ? ticks - 1 : delayed ? period - ticks : ticks;
                        description = FormattableString.Invariant($"PWM 比较占空比约 {100m * ticks / period:G10}%");
                    }
                    else
                    {
                        mode = c.CompareMode!;
                        compare = c.CompareTicks!.Value;
                        var top = center ? period : period - 1;
                        if (compare > top)
                        {
                            throw Error($"{module} CH{ch} 的 CCR 比较值应为 0～{top}（当前 ARR）。");
                        }
                        var action = mode switch
                        {
                            "TOGGLE" => "翻转",
                            "ACTIVE" => "置为有效",
                            _ => "置为无效"
                        };
                        description = $"CNT = CCR{ch} = {compare} 时输出{action}（ARR = {top}）";
                    }
                    details.Add($"{route} · {description}");
                    Emit($"/* {route}，{description}。 */");
                    Emit($"oc.OCMode = GPTIMER_OCMODE_{mode};");
                    if (advanced)
                    {
                        Emit($"oc.OCPolarity = GPTIMER_OC_POLARITY_{(setup.Value(module,"Pol"+ch) == "反相" ? "INVERTED" : "NONINVERTED")};");
                        Emit($"oc.OCNPolarity = GPTIMER_OC_POLARITY_{(setup.Value(module,"NPol"+ch) == "反相" ? "INVERTED" : "NONINVERTED")};");
                    }
                    Emit($"oc.OCNState = GPTIMER_OCSTATE_{(c.Complementary ? "ENABLE" : "DISABLE")};");
                    Emit($"oc.CompareValue = {compare}u;");
                    Emit($"GPTIMER_OC_Init({module}, GPTIMER_CHANNEL_CH{ch}, &oc);");
                    Emit($"GPTIMER_OC_EnablePreload({module}, GPTIMER_CHANNEL_CH{ch});");
                }
                else
                {
                    details.Add($"{route} · 输入捕获 · 每 {c.Divider} 个有效边沿" + (c.Interrupt ? $" · 回调 SX_{module}_Capture" : $" · 读取 CCR{ch}"));
                    var edge = c.Edge switch
                    {
                        "RISING" => "上升沿",
                        "FALLING" => "下降沿",
                        _ => "双边沿"
                    };
                    Emit($"/* {route}，按{edge}捕获，每 {c.Divider} 个有效边沿锁存一次计数值。 */");
                    Emit($"ic.ICPolarity = GPTIMER_IC_POLARITY_{c.Edge};");
                    Emit($"ic.ICFilter = {CaptureFilter(c.Filter)};");
                    Emit($"ic.ICPrescaler = GPTIMER_ICPSC_DIV{c.Divider};");
                    Emit($"GPTIMER_IC_Init({module}, GPTIMER_CHANNEL_CH{ch}, &ic);");
                }
            }
            if (s.DeadTimeNs > 0 || s.BreakPolarity is not null)
            {
                var (dtg, ns) = DeadTime(s, clock);
                Emit("GPTIMER_BDTR_InitTypeDef bdtr;");
                Emit("GPTIMER_BDTR_StructInit(&bdtr);");
                Emit($"bdtr.DeadTimeGenerator = {dtg}u;");
                details.Add(FormattableString.Invariant($"实际死区 {ns:G10} ns"));
                if (s.BreakPolarity is not null)
                {
                    details.Add($"BRK → {Pin("BRK", true)} · {(s.BreakPolarity == "HIGH" ? "高" : "低")}电平刹车 · 软件重新使能输出");
                    if (advanced)
                    {
                        Emit($"bdtr.BreakFilter = (GPTIMER_BreakFilterTypeDef)({setup.Number(module,"BreakFilter")}u << GPTIMER_BDTR_BKF_OFFSET);");
                    }
                    Emit("bdtr.BreakState = GPTIMER_BREAK_ENABLE;");
                    Emit($"bdtr.BreakPolarity = GPTIMER_BREAK_POLARITY_{s.BreakPolarity};");
                    Emit("bdtr.OSSIState = GPTIMER_OSSI_ENABLE;");
                    Emit("bdtr.OSSRState = GPTIMER_OSSR_ENABLE;");
                }
                Emit($"GPTIMER_BDTR_Init({module}, &bdtr);");
            }
            Emit($"GPTIMER_GenerateEventUpdate({module});");
            Emit($"GPTIMER_ClearFlagUpdate({module});");
            if (s.UpdateInterrupt || s.BreakInterrupt || channels.Any(c => c?.Interrupt == true))
            {
                if (s.UpdateInterrupt)
                {
                    Emit($"GPTIMER_SetUpdateSource({module}, GPTIMER_UPDATESOURCE_COUNTER);");
                    Emit($"GPTIMER_EnableIntUpdate({module});");
                }
                if (s.BreakInterrupt)
                {
                    Emit($"GPTIMER_EnableIntBreak({module});");
                }
                for (int ch = 0; ch < 4; ch++)

                {
                    if (channels[ch]?.Interrupt == true)
                    {
                        Emit($"GPTIMER_EnableIntCC{ch}({module});");
                    }
                }
                code.AppendLine(Ag32InterruptCode.Enable(module, s.InterruptPriority, "    "));
            }
            if (output)
            {
                Emit($"GPTIMER_EnableAllOutputs({module});");
            }
            if (!deferStart && setup.Units.ContainsKey(module))

            {
                foreach (var line in setup.TimerStart(module))
                {
                    Emit(line);
                }
            }
            else if (!deferStart)
            {
                Emit($"GPTIMER_EnableCounter({module});");
            }
        }
        timing = string.Join("\n", details);
        return code.Append('}').ToString();
    }

    // ICxF 为四位。SDK 后六个枚举把十进制 10–15 写成 0x10–0x15，会越入相邻通道。
    private static string CaptureFilter(int filter) => $"(GPTIMER_IC_FilterTypeDef)({filter}u << GPTIMER_CCMR_IC0F_OFFSET)";

    private static (int Code, decimal Nanoseconds) DeadTime(Ag32TimerSettings s, uint clock)
    {
        if (s.DeadTimeNs > 1008m * 1_000_000_000m / clock)

        {
            throw Error($"{s.Module} 死区超过当前时钟可产生的最大值。");
        }
        var ticks = (int)decimal.Ceiling(s.DeadTimeNs * clock / 1_000_000_000m);
        // 向上取整到硬件可表达的延时，跨分段时进到下一段起点。
        var (step, offset) = ticks <= 127 ? (1, 0) : ticks <= 254 ? (2, 64) : ticks <= 504 ? (8, 160) : (16, 192);
        var count = (ticks + step - 1) / step;
        return (offset + count, count * (decimal)step * 1_000_000_000m / clock);
    }

    internal static void BasicHandlers(Ag32TimerSettings[] timers, StringBuilder api, StringBuilder handlers)
    {
        foreach (var group in timers.Where(t => IsBasic(t.Module) && t.UpdateInterrupt).GroupBy(t => t.Module[..6]))
        {
            var module = group.Key;
            Ag32InterruptCode.SharedPriority(module, group.Select(s => s.InterruptPriority));
            Ag32InterruptCode.Callback(api, handlers, $"void SX_{module}_PeriodElapsed(uint32_t channel)",
                $"{module} 周期到达时调用，channel 是触发中断的计数器编号。", "(void)channel;");
            handlers.AppendLine($"void {module}_isr(void)").AppendLine("{");
            foreach (var timer in group)
            {
                var n = timer.Module[^1];
                handlers.AppendLine($"    if (TIM_IsMaskedIntActive{n}({module}))").AppendLine("    {")
                    .AppendLine($"        TIM_ClearInt{n}({module});").AppendLine($"        SX_{module}_PeriodElapsed({n}u);").AppendLine("    }");
            }
            handlers.AppendLine("}");
        }
    }

    internal static void GeneralHandler(Ag32TimerSettings s, StringBuilder api, StringBuilder handlers)
    {
        var channels = s.Channels;
        if (!IsGeneral(s.Module) || !s.UpdateInterrupt && !s.BreakInterrupt && !channels.Any(c => c?.Interrupt == true))
        {
            return;
        }
        var module = s.Module;
        if (channels.Any(c => c is { Interrupt: true, Edge: not null }))
        {
            api.AppendLine("/* ticks 是捕获时的计数，overcapture 表示发生覆盖。 */");
            Ag32InterruptCode.Callback(api, handlers, $"void SX_{module}_Capture(uint32_t channel, uint32_t ticks, bool overcapture)",
                "输入捕获中断。channel 为通道号，ticks 为捕获计数，overcapture 表示发生覆盖。",
                "(void)channel; (void)ticks; (void)overcapture;");
        }
        if (channels.Any(c => c is { Interrupt: true, IsOutput: true }))

        {
            Ag32InterruptCode.Callback(api, handlers, $"void SX_{module}_Compare(uint32_t channel)",
                            "输出比较匹配时调用，channel 为匹配的通道号。", "(void)channel;");
        }
        if (s.UpdateInterrupt)
        {
            Ag32InterruptCode.Callback(api, handlers, $"void SX_{module}_PeriodElapsed(void)",
                        "计数器溢出或下溢产生更新中断时调用。");
        }
        if (s.BreakInterrupt)
        {
            Ag32InterruptCode.Callback(api, handlers, $"void SX_{module}_Break(void)", "刹车输入触发中断时调用。");
        }
        handlers.AppendLine($"void {module}_isr(void)").AppendLine("{");
        void Flag(string flag, string callback) =>
            handlers.AppendLine($"    if (GPTIMER_IsEnabledInt{flag}({module}) && GPTIMER_IsActiveFlag{flag}({module}))").AppendLine("    {")
                .AppendLine($"        GPTIMER_ClearFlag{flag}({module});").AppendLine($"        SX_{module}_{callback};").AppendLine("    }");
        if (s.UpdateInterrupt)
        {
            Flag("Update", "PeriodElapsed()");
        }
        if (s.BreakInterrupt)
        {
            Flag("Break", "Break()");
        }
        for (int ch = 0; ch < 4; ch++)
        {
            if (channels[ch]?.Interrupt != true)
            {
                continue;
            }
            if (channels[ch]?.IsOutput == true)
            {
                Flag($"CC{ch}", $"Compare({ch}u)");
                continue;
            }
            handlers.AppendLine($"    if (GPTIMER_IsEnabledIntCC{ch}({module}) && GPTIMER_IsActiveFlagCC{ch}({module}))")
                .AppendLine("    {")
                .AppendLine($"        uint32_t ticks = GPTIMER_IC_GetCapture({module}, GPTIMER_CHANNEL_CH{ch});")
                .AppendLine($"        bool overcapture = GPTIMER_IsActiveFlagCC{ch}Over({module});")
                .AppendLine($"        if (overcapture) GPTIMER_ClearFlagCC{ch}Over({module});")
                .AppendLine($"        SX_{module}_Capture({ch}u, ticks, overcapture);")
                .AppendLine("    }");
        }
        handlers.AppendLine("}");
    }
}
