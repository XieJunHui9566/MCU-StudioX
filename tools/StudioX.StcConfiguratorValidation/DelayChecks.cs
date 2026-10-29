namespace StudioX.StcConfiguratorValidation;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.StcConfigurator;

internal static class DelayChecks
{
    internal static async Task RunAsync(string contents, string runtime, string output, Action<bool, string> check, Action<Action, string> reject)
    {
        var compiler = Path.Combine(runtime, "toolsets/stc.sdcc/1.0.0/sdcc/bin/sdcc.exe");
        var environment = new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(compiler) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };
        var model = new StcConfigurationModel(contents);
        void Set(params (string Key, string Value)[] changes) => model.Apply(JsonSerializer.SerializeToElement(new
        {
            action = "parameters",
            values = changes.ToDictionary(p => p.Key, p => p.Value)
        }));
        Set(("clockSource", "IRC"), ("clockMHz", "12"), ("reserve", "none"));
        foreach (var label in new[] { "Delay_ms", "XMS" })
        {
            reject(() => model.Apply(JsonSerializer.SerializeToElement(new { action = "pinLabel", pinId = "pad-4", label })), "pin alias cannot shadow software Delay symbol " + label);
        }
        check(model.GenerateFiles().Single(f => f.Path.EndsWith("/mcuconfig.h", StringComparison.Ordinal)).Content.Contains("generated/mcuconfig/Delay.h", StringComparison.Ordinal), "every generated entry header exposes Delay_ms without another user include");
        var evidence = new List<object>();
        // 覆盖 STC-ISP 参考点、系统分频、分数周期、双循环临界值及最低可表示系统频率。
        var cases = new[] { (12000000, 1), (11059200, 1), (24000000, 2), (27000000, 1), (28000000, 1), (1000064, 128), (2334000, 1), (2343000, 1), (2352000, 1), (12345678, 1) };
        var optimizations = new (string Name, string[] Flags)[]
        {
            ("default", []), ("Os", ["--opt-code-size"]), ("O2", ["--opt-code-speed"]),
            ("O0", ["--no-peep", "--nogcse", "--noinvariant", "--noinduction", "--noloopreverse", "--nolabelopt", "--nolospre"])
        };
        foreach (var ((sourceHz, divider), (mode, flags)) in cases.SelectMany(c => optimizations.Select(o => (c, o))))
        {
            var hz = sourceHz / divider;
            Set(("clockSource", sourceHz < 5000000 ? "XTAL" : "IRC"), ("clockMHz", (sourceHz / 1000000m).ToString(CultureInfo.InvariantCulture)), ("systemDivider", divider.ToString(CultureInfo.InvariantCulture)));
            var files = model.GenerateFiles();
            var directory = Path.Combine(output, "delay", sourceHz + "-div" + divider + "-" + mode);
            Directory.CreateDirectory(directory);
            var source = files.Single(f => f.Path.EndsWith("/Delay.c", StringComparison.Ordinal)).Content;
            var header = files.Single(f => f.Path.EndsWith("/Delay.h", StringComparison.Ordinal)).Content;
            check(!Regex.IsMatch(source + header, @"\b(?:__asm|__endasm|__naked|__sdcccall|_nop_)\b") &&
                header.Contains("void Delay_ms(uint16_t XMS);", StringComparison.Ordinal), "Delay uses plain C and a standard C declaration: " + mode);
            var include = Path.Combine(directory, "generated/mcuconfig");
            Directory.CreateDirectory(include);
            await File.WriteAllTextAsync(Path.Combine(directory, "Delay.c"), source);
            await File.WriteAllTextAsync(Path.Combine(include, "Delay.h"), header);
            await File.WriteAllTextAsync(Path.Combine(directory, "main.c"), "#include \"generated/mcuconfig/Delay.h\"\nvolatile uint16_t delay_argument = 257U;\nvoid main(void) { Delay_ms(delay_argument); for (;;) {} }\n");
            async Task<ProcessResult> Compile(string log, params string[] arguments)
            {
                var result = await new ProcessRunner().RunAsync(new(compiler, arguments, directory, TimeSpan.FromSeconds(30), Environment: environment));
                await File.WriteAllTextAsync(Path.Combine(directory, log), result.StandardOutput + result.StandardError);
                return result;
            }
            var compiled = await Compile("delay.log", ["-mmcs51", "--std-c11", .. flags, "-DIAP15L2K61S2", "-DSTUDIOX_STC15", "-DSTUDIOX_CLOCK_HZ=" + hz, "-DSTUDIOX_CLOCK_DIVIDER=" + divider, "-I.", "-c", "Delay.c"]);
            var caller = await Compile("main.log", "-mmcs51", "--std-c11", "-I.", "-c", "main.c");
            var linked = await Compile("link.log", "-mmcs51", "main.rel", "Delay.rel", "-o", "delay.ihx");
            check(compiled.Success && caller.Success && linked.Success, "SDCC compiles/links Delay.c and variable uint16_t caller: " + sourceHz + "/" + divider + " " + mode);
            var map = await File.ReadAllTextAsync(Path.Combine(directory, "delay.map"));
            var match = Regex.Match(map, @"(?m)^\s*(?:C:\s*)?([0-9A-Fa-f]{8})\s+_Delay_ms\s");
            if (!match.Success)
            {
                throw new InvalidOperationException("Linked Delay_ms address is missing: " + map);
            }
            var address = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var image = DelayInstructionChecks.ReadHex(await File.ReadAllTextAsync(Path.Combine(directory, "delay.ihx")));
            var runs = new List<object>();
            foreach (var milliseconds in mode == "O0" ? [0, 1, 2] : new[] { 0, 1, 2, 255, 256, 257, 65535 })
            {
                var cycles = DelayInstructionChecks.Execute(image, address, milliseconds);
                // 直接执行链接后的机器码，对照目标时间；容差包括 C 循环量化及极低频下最小开销。
                // O0 只验证有限返回和未删除等待，不把未优化 C 代码冒充已校准的毫秒精度。
                var target = 20m + milliseconds * (hz / 1000m) + 2 * (milliseconds / 256);
                var tolerance = milliseconds * Math.Max(9m, 39m - hz / 1000m);
                var valid = mode == "O0"
                    ? milliseconds == 0 ? cycles <= 30 : cycles >= target && cycles <= 2 * (target + tolerance)
                    : Math.Abs(cycles - target) <= tolerance;
                check(valid, "linked C instruction audit: SYSCLK " + hz + " Hz, " + mode + ", XMS " + milliseconds + " -> " + cycles);
                runs.Add(new
                {
                    milliseconds,
                    cycles,
                    nominalMilliseconds = cycles * 1000m / hz
                });
            }
            var staleClock = await Compile("stale-clock.log", "-mmcs51", "-DIAP15L2K61S2", "-DSTUDIOX_STC15", "-DSTUDIOX_CLOCK_HZ=" + (hz + 1), "-I.", "-c", "Delay.c", "-o", "rejected.rel");
            check(!staleClock.Success && staleClock.StandardError.Contains("Clock_changed_regenerate_Delay", StringComparison.Ordinal), "stale clock cannot compile old software delay: " + hz);
            evidence.Add(new
            {
                sourceHz,
                divider,
                systemHz = hz,
                optimization = mode,
                timingCalibrated = mode != "O0",
                runs
            });
        }
        await JsonStore.WriteAsync(Path.Combine(output, "delay-cycles.json"), new
        {
            hardware = false,
            method = "Linked machine code interpreted with STC15 manual section 5.2 instruction clocks; not wall-clock or hardware measurement",
            evidence
        });
    }
}
