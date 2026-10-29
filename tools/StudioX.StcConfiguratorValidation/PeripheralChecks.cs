namespace StudioX.StcConfiguratorValidation;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;
using StudioX.Packages;
using StudioX.StcConfigurator;

internal static class PeripheralChecks
{
    internal static async Task RunAsync(InstalledPack pack, string archive, string runtime, string output, string project, string contents,
        Action<bool, string> check, Action<Action, string> reject)
    {
        static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
        static void Set(StcConfigurationModel model, params (string Key, string Value)[] changes)
            => model.Apply(Json(new
            {
                action = "parameters",
                values = changes.ToDictionary(p => p.Key, p => p.Value)
            }));
        StcConfigurationModel New(params (string Key, string Value)[] changes)
        {
            var model = new StcConfigurationModel(contents);
            Set(model, ("clockSource", "IRC"), ("clockMHz", "24"), ("reserve", "none"));
            Set(model, changes);
            return model;
        }
        PluginMcuConfigurationState View(StcConfigurationModel model) => PluginMcuConfiguration.Parse(Json(model.View("", true)));
        void Valid(StcConfigurationModel model, string name) => check(model.Errors().Count == 0, name + ": " + string.Join("; ", model.Errors()));
        var initial = New();
        var ids = initial.Document["definitions"]!["peripherals"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).ToArray();
        check(new[] { "GPIO5", "TIMER0", "TIMER1", "TIMER2", "UART1", "UART2", "ADC", "SPI", "PCA", "EXTI", "WDT", "WKT", "LVD", "IAP", "MCLKO" }.All(ids.Contains) && !ids.Contains("TIMER3") && !ids.Contains("CMP"), "exact model inventory includes supported resources without borrowing W4K peripherals");
        check(View(initial).Fields.Length <= 64 && View(initial).Pins.Count(p => p.Functions.Any(f => f.Value == "gpio")) == 42, "full view fits current host contract and exposes 42 GPIOs");
        var timer2 = New(("timer2", "12"), ("period2", "1000"));
        Valid(timer2, "standalone Timer2");
        check(timer2.GenerateCode().Contains("T2L = 0x30;", StringComparison.Ordinal) && timer2.GenerateCode().Contains("T2H = 0xF8;", StringComparison.Ordinal) && View(timer2).Clocks.Single(c => c.Id == "T2").Value.Contains("停止", StringComparison.Ordinal), "Timer2 12T reload and clock diagram reflect peripheral settings");
        foreach (var clock in new[] { "T1-1", "T1-12", "T2-1", "T2-12" })
        {
            var uart = New(("uartRoute", "p36"), ("uartClock", clock));
            Valid(uart, "UART1 " + clock);
            check(View(uart).Clocks.Single(c => c.Id == "UART1").ActiveInputIds!.SequenceEqual(new[] { clock[..2] }), "UART1 clock tree selects " + clock);
            Set(uart, ("timer" + clock[1], "1"));
            check(uart.Errors().Any(e => e.Contains("占用", StringComparison.Ordinal)), "UART1 timer ownership rejects independent use: " + clock);
        }
        var shared = New(("uartRoute", "p36"), ("uart2Route", "p46"));
        Valid(shared, "shared Timer2 same baud");
        check(shared.GenerateCode().Split("T2H =", StringSplitOptions.None).Length == 2, "shared UART Timer2 initialized once");
        Set(shared, ("baudRate2", "19200"));
        check(shared.Errors().Any(e => e.Contains("必须相同", StringComparison.Ordinal)), "different baud rejects shared Timer2");
        Set(shared, ("baudRate2", "9600"), ("uart2Divider", "12"));
        check(shared.Errors().Any(e => e.Contains("必须相同", StringComparison.Ordinal)), "different divider rejects shared Timer2");
        Set(shared, ("uartClock", "T1-1"));
        Valid(shared, "two UARTs with independent timers");
        var conflict = New(("uart2Route", "default"));
        var before = conflict.Document.ToJsonString();
        reject(() => Set(conflict, ("adcSpeed", "90"), ("adc0", "1")), "ADC/UART2 pin overlap rejected");
        check(before == conflict.Document.ToJsonString(), "pin conflict rolls back entire candidate");
        conflict.Apply(Json(new
        {
            action = "pinLock",
            pinId = "pad-4",
            locked = true
        }));
        reject(() => Set(conflict, ("uart2Route", "p46")), "new peripheral remap respects pin lock");
        var external = New(("clockSource", "XTAL"), ("adcSpeed", "180"), ("adc6", "1"));
        check(external.Errors().Any(e => e.Contains("晶振", StringComparison.Ordinal)), "ADC6 conflicts with external crystal");
        Set(external, ("adc6", "0"), ("adc2", "1"));
        Valid(external, "ADC with crystal on separate pins");
        check(View(external).Clocks.Single(c => c.Id == "ADC").Value.Contains("内部 IRC", StringComparison.Ordinal), "ADC clock never borrows crystal frequency");
        var pca = New(("pcaRoute", "p2"), ("pcaClock", "T0"), ("pcaMode0", "pwm8"));
        check(pca.Errors().Any(e => e.Contains("Timer0", StringComparison.Ordinal)), "PCA Timer0 source requires Timer0 configuration");
        Set(pca, ("timer0", "1"));
        Valid(pca, "PCA Timer0 clock");
        Set(pca, ("pcaValue0", "257"));
        reject(() => pca.GenerateFiles(), "PWM duty above 256 rejected");
        foreach (var duty in new[] { "0", "128", "256" })
        {
            Set(pca, ("pcaValue0", duty));
            Valid(pca, "PWM duty boundary " + duty);
            check(pca.GenerateCode().Contains("PCA_PWM0 = " + (duty == "0" ? "0x03" : "0x00"), StringComparison.Ordinal), "PWM extended compare bits for duty " + duty);
        }
        reject(() => Set(initial, ("int2", "both")), "INT2 cannot select unsupported both-edge trigger");
        reject(() => Set(initial, ("wakeEnabled", "1"), ("wakeCounts", "32769")), "wake timer overflow rejected");
        var p54 = New();
        p54.Apply(Json(new
        {
            action = "assignPin",
            pinId = "pad-13",
            functionId = "gpio"
        }));
        check(p54.Errors().Any(e => e.Contains("复位", StringComparison.Ordinal)), "P5.4 requires ISP reset option confirmation");
        Set(p54, ("p54Gpio", "1"));
        Valid(p54, "P5.4 confirmed GPIO");
        var mclk = New(("mclko", "1"), ("p54Gpio", "1"));
        check(mclk.Errors().Count > 0, "MCLKO maximum 8 MHz checked");
        Set(mclk, ("mclko", "4"));
        Valid(mclk, "MCLKO 6 MHz");
        var tampered = JsonNode.Parse(timer2.Document.ToJsonString())!;
        tampered["configuration"]!["clocks"]!.AsArray().Single(n => n!["nodeId"]!.GetValue<string>() == "T2")!["parameters"]!["divider"] = 1;
        check(new StcConfigurationModel(tampered.ToJsonString()).Errors().Any(e => e.Contains("不一致", StringComparison.Ordinal)), "stored timer/clock disagreement blocks generation");
        var full = New(("uartRoute", "p16"), ("uartClock", "T1-12"), ("uart2Route", "p46"), ("timer0", "12"),
            ("adcSpeed", "90"), ("adc0", "1"), ("adc1", "1"), ("adc2", "1"), ("spiRoute", "p4"), ("spiMode", "3"),
            ("pcaRoute", "p2"), ("pcaMode0", "pwm8"), ("pcaMode1", "rising"), ("pcaMode2", "toggle"),
            ("int0", "both"), ("int1", "falling"), ("int2", "falling"), ("int3", "falling"), ("int4", "falling"),
            ("wdtDivider", "256"), ("wdtIdle", "1"), ("wakeEnabled", "1"), ("wakeCounts", "32768"), ("lvdEnabled", "1"),
            ("iapEnabled", "1"), ("mclko", "4"), ("p54Gpio", "1"));
        full.Apply(Json(new
        {
            action = "assignPin",
            pinId = "pad-40",
            functionId = "gpio"
        }));
        full.Apply(Json(new
        {
            action = "pinLabel",
            pinId = "pad-40",
            label = "LED"
        }));
        reject(() => full.Apply(Json(new { action = "pinLabel", pinId = "pad-40", label = "SX_ADC_CHANNEL_MASK" })), "pin alias cannot shadow new peripheral constants");
        Valid(full, "all supported peripheral modules coexist on nonoverlapping routes");
        check(View(full).Fields.Length <= 64 && full.GenerateFiles().Length == 28, "full configuration yields 14 source/header pairs including software Delay within host limits");
        var code = full.GenerateCode();
        check(code.Contains("#define LED P00", StringComparison.Ordinal) && !code.Contains("__interrupt", StringComparison.Ordinal) && !code.Contains("EA = 1", StringComparison.Ordinal) && !code.Contains("SPDAT =", StringComparison.Ordinal) && !code.Contains("IAP_TRIG = 0x5A", StringComparison.Ordinal), "init-only generation retains aliases without business ISR, transfers or flash trigger");
        check(code.Contains("WKTCL = 0xFF", StringComparison.Ordinal) && code.Contains("WKTCH = 0xFF", StringComparison.Ordinal), "wake timer 32768 boundary encoded as 32767 with enable bit");
        check(new StcConfigurationModel(full.Document.ToJsonString()).GenerateCode() == code, "all peripheral parameters survive save/reopen without code drift");
        Set(full, ("uart1Printf", "1"), ("uart2Printf", "1"));
        await PrintfChecks.RunAsync(pack, contents, runtime, output, check, reject);
        await DelayChecks.RunAsync(contents, runtime, output, check, reject);

        var builder = new BuildService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
        var originalMain = await File.ReadAllTextAsync(Path.Combine(project, "src/main.c"));
        var matrix = new List<(string Name, StcConfigurationModel Model)> { ("all-peripherals", full), ("timer2-12T", timer2), ("pca-zero", pca), ("adc-external-clock", external) };
        foreach (var source in new[] { "T1-1", "T1-12", "T2-1", "T2-12" })
        {
            matrix.Add(("uart-" + source, New(("uartRoute", "p36"), ("uartClock", source), ("uart1Printf", "1"))));
        }
        for (var index = 0; index < 3; index++)
        {
            matrix.Add(("counter" + index, New(("timer" + index, "external"), ("period" + index, "65536"), ("timerOutput" + index, "1"))));
            matrix.Add(("spi-mode" + index, New(("spiRoute", new[] { "p1", "p2", "p4" }[index]), ("spiMode", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("spiOrder", "1"))));
        }
        foreach (var (name, model) in matrix)
        {
            Valid(model, "compile input " + name);
            model.PrepareClockValidation();
            await File.WriteAllTextAsync(Path.Combine(project, "device.mcuconfig"), model.Document.ToJsonString(), new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), model.GenerateCode() + "\nvoid main(void) { sx_mcuconfig_init(); for (;;) { } }\n", new UTF8Encoding(false));
            var result = await builder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), result.Log);
            if (!result.Success)
            {
                throw new InvalidOperationException(name + "\n" + result.Log);
            }
            check(true, "real SDCC compile/link: " + name);
        }
        full.PrepareClockValidation();
        await File.WriteAllTextAsync(Path.Combine(project, "device.mcuconfig"), full.Document.ToJsonString(), new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), originalMain, new UTF8Encoding(false));
        var host = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory)))!, "StudioX.PluginHost/release_win-x64");
        var hostRuntime = Path.Combine(output, "host-runtime");
        var destination = Path.Combine(hostRuntime, "plugin-host");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(host))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        await using (var services = new WorkbenchService(hostRuntime, Path.Combine(output, "user-data")))
        {
            var entry = await services.PluginManager.ImportAsync(archive);
            await services.PluginManager.SetEnabledAsync(entry.Id, true);
            await using var broker = new PluginWorkspaceBroker(services, project, new ConfigurationWriteAuthorizer(), () => Task.FromResult(false));
            await using var workspace = await services.PluginManager.OpenWorkspaceAsync(project, broker.CallAsync);
            var state = await workspace.InvokeAsync(entry.Id, "command", "interact", Json(new
            {
                action = "validate"
            }));
            check(PluginMcuConfiguration.Parse(state).CanSave, "isolated plugin host opens expanded project profile");
            check(await services.McuCodeGeneration.GenerateAsync(workspace, entry.Id, "interact", () => Task.FromResult(false)) == 28, "host transaction generates all peripheral modules and Delay, and integrates main/CMake");
            await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
            var built = await builder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(output, "modules.log"), built.Log);
            if (!built.Success)
            {
                throw new InvalidOperationException(built.Log);
            }
            check(true, "real SDCC builds split modules through generated CMake registration");
            var mainPath = Path.Combine(project, "src/main.c");
            var generatedMain = await File.ReadAllTextAsync(mainPath);
            var userMain = generatedMain.Replace("    sx_mcuconfig_init();", "    sx_mcuconfig_init();\n    USART1_printf(\"HelloWorld!\\r\\n\");\n    USART2_printf(\"%04X\", 0x2au);\n    Delay_ms(1000U);", StringComparison.Ordinal);
            await File.WriteAllTextAsync(mainPath, userMain);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                await services.McuCodeGeneration.GenerateAsync(workspace, entry.Id, "interact", () => Task.FromResult(false));
                await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
                check(await File.ReadAllTextAsync(mainPath) == userMain, "repeated host generation preserves printf and Delay inside init markers: " + repeat);
            }
            built = await builder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(output, "printf-split-modules.log"), built.Log);
            check(built.Success, "main calls printf and Delay_ms via automatic header and separately compiled modules: " + (built.Success ? "passed" : built.Log));
            await File.WriteAllTextAsync(mainPath, generatedMain);
            // 关闭新增模块后，宿主必须清理其拥有的旧文件与构建引用。
            var minimal = new StcConfigurationModel(await File.ReadAllTextAsync(Path.Combine(project, "device.mcuconfig")));
            Set(minimal, ("uartRoute", "off"), ("uart2Route", "off"), ("timer0", "off"), ("adcSpeed", "off"), ("spiRoute", "off"), ("pcaRoute", "off"), ("int0", "off"), ("int1", "off"), ("int2", "off"), ("int3", "off"), ("int4", "off"), ("wdtDivider", "off"), ("wakeEnabled", "0"), ("lvdEnabled", "0"), ("iapEnabled", "0"), ("mclko", "off"));
            var values = minimal.View("", true).Fields.ToDictionary(f => f.Id, f => f.Value);
            await workspace.InvokeAsync(entry.Id, "command", "interact", Json(new
            {
                action = "parameters",
                values
            }));
            check(await services.McuCodeGeneration.GenerateAsync(workspace, entry.Id, "interact", () => Task.FromResult(false)) == 10 && !File.Exists(Path.Combine(project, "src/generated/mcuconfig/adc.c")) && File.Exists(Path.Combine(project, "src/generated/mcuconfig/Delay.c")), "disabling peripherals removes owned stale modules and retains software Delay");
            check(!(await File.ReadAllTextAsync(Path.Combine(project, "src/generated/mcuconfig/uart.c"))).Contains("_print_format", StringComparison.Ordinal) && !(await File.ReadAllTextAsync(Path.Combine(project, "include/generated/mcuconfig/uart.h"))).Contains("_printf", StringComparison.Ordinal), "host regeneration removes disabled printf implementation and declarations");
            await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
            var delayPath = Path.Combine(project, "src/generated/mcuconfig/Delay.c");
            var beforeDelay = await File.ReadAllTextAsync(delayPath);
            await workspace.InvokeAsync(entry.Id, "command", "interact", Json(new
            {
                action = "parameters",
                values = new Dictionary<string, string> { ["systemDivider"] = "2" }
            }));
            await services.McuCodeGeneration.GenerateAsync(workspace, entry.Id, "interact", () => Task.FromResult(false));
            var dividedDelay = await File.ReadAllTextAsync(delayPath);
            check(dividedDelay != beforeDelay && dividedDelay.Contains("SYSCLK 12000000 Hz", StringComparison.Ordinal), "host regeneration recalculates Delay from divided SYSCLK rather than oscillator frequency");
            await services.McuCodeGeneration.RefreshAsync(workspace, entry.Id, "interact");
            built = await builder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(output, "delay-divided-modules.log"), built.Log);
            check(built.Success, "host regenerated Delay compiles after system divider change: " + (built.Success ? "passed" : built.Log));
            await workspace.InvokeAsync(entry.Id, "command", "interact", Json(new
            {
                action = "parameters",
                values = full.View("", true).Fields.Where(f => f.Id is not ("gpioMode" or "gpioLevel")).ToDictionary(f => f.Id, f => f.Value)
            }));
            await services.McuCodeGeneration.GenerateAsync(workspace, entry.Id, "interact", () => Task.FromResult(false));
        }
        await JsonStore.WriteAsync(Path.Combine(output, "compile-matrix.json"), matrix.Select(m => m.Name).ToArray());
    }
}
