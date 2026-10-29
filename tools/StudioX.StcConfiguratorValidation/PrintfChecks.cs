namespace StudioX.StcConfiguratorValidation;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;
using StudioX.StcConfigurator;

internal static class PrintfChecks
{
    internal static async Task RunAsync(InstalledPack pack, string contents, string runtime, string output, Action<bool, string> check, Action<Action, string> reject)
    {
        static void Set(StcConfigurationModel model, params (string Key, string Value)[] changes) => model.Apply(JsonSerializer.SerializeToElement(new
        {
            action = "parameters",
            values = changes.ToDictionary(p => p.Key, p => p.Value)
        }));
        var model = new StcConfigurationModel(contents);
        Set(model, ("clockSource", "IRC"), ("clockMHz", "24"), ("reserve", "none"), ("uartRoute", "p36"), ("uartClock", "T1-12"), ("uart2Route", "p46"));
        var disabled = model.Document.ToJsonString();
        check(model.View("", true).Fields.Where(f => f.Id is "uart1Printf" or "uart2Printf").All(f => f.Value == "0") && !model.GenerateCode().Contains("_print_format", StringComparison.Ordinal), "UART printf defaults off in existing projects without linking formatting code");
        foreach (var index in new[] { 1, 2 })
        {
            Set(model, ("uart" + index + "Printf", "1"));
            var header = model.GenerateFiles().Single(f => f.Path == "include/generated/mcuconfig/uart.h").Content;
            check(header.Contains("int USART" + index + "_printf(const char *sx_format, ...);", StringComparison.Ordinal) && !header.Contains("int USART" + (3 - index) + "_printf", StringComparison.Ordinal), "only selected UART exposes variadic printf: " + index);
            Set(model, ("uart" + index + "Printf", "0"));
            check(model.Document.ToJsonString() == disabled, "switch off removes optional setting without changing legacy configuration: " + index);
        }
        Set(model, ("uart1Printf", "1"), ("uart2Printf", "1"));
        var reopened = new StcConfigurationModel(model.Document.ToJsonString());
        check(reopened.GenerateCode() == model.GenerateCode() && reopened.View("", true).Fields.Length <= 64, "both UART printf settings survive save/reopen within field budget");
        check(model.GenerateFiles().Single(f => f.Path == "include/generated/mcuconfig/mcuconfig.h").Content.Contains("generated/mcuconfig/uart.h", StringComparison.Ordinal), "main entry header exposes selected UART printf without manual includes");
        var before = model.Document.ToJsonString();
        reject(() => Set(model, ("uart1Printf", "enabled")), "invalid printf switch rejected");
        check(model.Document.ToJsonString() == before, "invalid switch leaves entire configuration unchanged");
        foreach (var value in new JsonNode?[] { JsonValue.Create("true"), JsonValue.Create(1), null })
        {
            var invalid = model.Document.DeepClone();
            invalid["configuration"]!["peripherals"]!.AsArray().Single(p => p!["peripheralId"]!.GetValue<string>() == "UART1")!["parameters"]!["printfEnabled"] = value;
            reject(() => _ = new StcConfigurationModel(invalid.ToJsonString()), "stored printf flag requires JSON boolean: " + (value?.ToJsonString() ?? "null"));
        }
        foreach (var label in new[] { "USART1_printf", "USART2_printf" })
        {
            var pin = model.View("", true).Pins.Single(p => p.Name == "P3.6").Id;
            reject(() => model.Apply(JsonSerializer.SerializeToElement(new { action = "pinLabel", pinId = pin, label })), "GPIO alias cannot shadow " + label);
        }
        Set(reopened, ("uartRoute", "off"));
        check(!reopened.GenerateCode().Contains("int USART1_printf", StringComparison.Ordinal) && reopened.GenerateCode().Contains("int USART2_printf", StringComparison.Ordinal), "disabling UART removes its helper and preserves the other UART");

        var project = Path.Combine(output, "printf-project");
        await new ProjectService().CreateAsync(pack, "IAP15L2K61S2", "bare", "printf_validation", project);
        model.PrepareClockValidation();
        await File.WriteAllTextAsync(Path.Combine(project, "device.mcuconfig"), model.Document.ToJsonString());
        var main = model.GenerateCode() + FirmwareChecks;
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), main, new UTF8Encoding(false));
        var builder = new BuildService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
        var build = await builder.BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(output, "printf-compile.log"), build.Log);
        check(build.Success, "real SDCC compile/link for printf formatting, return values and failure paths: " + (build.Success ? "passed" : build.Log));

        // C52 不模拟 STC 扩展外设。只模拟 TX 寄存器写入/完成标志，执行真实 SDCC 固件和格式库；不是串口时序或实板验收。
        var simulator = Path.Combine(runtime, "toolsets/stc.sdcc/1.0.0/sdcc/bin/ucsim_51.exe");
        // 条件表达式在写入前观察旧 SBUF、补充完成标志并返回 false，使模拟器连续运行。
        // 最后一次 SBUF 在完成断点读回；观察缓冲位于固件声明内存之外，不占用被测程序 RAM。
        var setup = "expression xram[0x700]=0; expression xram[0x701]=0; " +
            "break sfr w 0x99 1 if \"(xram[0x800+xram[0x700]++]=sfr[0x99],sfr[0x98]=sfr[0x98]|2,0)\"; " +
            "break sfr w 0x9b 1 if \"(xram[0x900+xram[0x701]++]=sfr[0x9b],sfr[0x9a]=sfr[0x9a]|((xram[0x6fe]==0)*2),0)\"; " +
            "break xram w 0x6ff; commands 3 echo SX_RESULT; expression /d xram[0x6fd]; expression /x xram[0x6ff]; " +
            "echo SX_UART1; expression /d xram[0x700]; expression /x sfr[0x99]; dump xram 0x800 0x8ff 16; " +
            "echo SX_UART2; expression /d xram[0x701]; expression /x sfr[0x9b]; dump xram 0x900 0x9ff 16";
        var arguments = new List<string> { "-q", "-G", "-t", "C52", "-e", setup };
        arguments.Add(build.Artifacts.First(p => p.EndsWith(".ihx", StringComparison.OrdinalIgnoreCase)));
        await JsonStore.WriteAsync(Path.Combine(output, "printf-simulation-input.json"), new
        {
            simulator,
            arguments,
            hardware = false,
            txCompletion = "register fixture, not hardware timing"
        });
        var simulation = await SimulateAsync(simulator, arguments, project);
        var log = simulation.StandardOutput.Replace("\r", "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(output, "printf-simulation.log"), log + simulation.StandardError);
        check(simulation.Success && !simulation.OutputTruncated, "8051 instruction simulation exits without timeout or truncated diagnostics");
        string Bytes(int index)
        {
            var marker = Regex.Match(log, "(?m)^SX_UART" + index + "\\n([0-9]+)\\n([0-9a-f]+)\\n");
            check(marker.Success, "simulator captured UART" + index + " byte count");
            var count = int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);
            var data = Regex.Matches(log, "(?m)^0x0" + (index == 1 ? "8" : "9") + "[0-9a-f]{2} +((?:[0-9a-f]{2} +){16})").SelectMany(m => Regex.Matches(m.Groups[1].Value, "[0-9a-f]{2}").Select(b => (char)int.Parse(b.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))).ToArray();
            check(data.Length == 256 && count is > 0 and < 256, "bounded UART" + index + " register capture is complete");
            return new string(data.Skip(1).Take(count - 1).Append((char)int.Parse(marker.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray());
        }
        var uart1 = Bytes(1);
        var uart2 = Bytes(2);
        var expected1 = "U1:abc -12 65535 002A 123456 % !\r\n" + new string('x', 180);
        const string expected2 = "U2:[     abc] [abc] [+00042] [7   ]\n[  abc]\nLR";
        await JsonStore.WriteAsync(Path.Combine(output, "printf-simulation-output.json"), new
        {
            uart1,
            uart2,
            expected1,
            expected2
        });
        check(uart1 == expected1, "SDCC formatter emits exact UART1 integer/string/hex/long/percent/CRLF and 180-byte output without truncation");
        check(uart2 == expected2, "UART2 formats widths/precision independently; timeout retries do not overwrite unfinished TX byte");
        check(log.Contains("SX_RESULT\n0\na5\n", StringComparison.Ordinal), "firmware confirms counts, empty format, NULL, IRQ/clock rejection, shared busy guard, EA/RI preservation and timeout recovery");
    }

    private static async Task<ProcessResult> SimulateAsync(string executable, List<string> arguments, string project)
    {
        // Windows uCsim 在重定向 stdin 时会提前退出或等待控制台。
        // 此验收固定使用 -G 自动运行到完成断点，不读输入；不更改生产工具进程的 stdin 隔离策略。
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            process.Kill(true);
            await process.WaitForExitAsync();
        }
        return new(process.ExitCode, await stdout, await stderr, timedOut, false);
    }

    private const string FirmwareChecks = """

        __xdata __at(0x6fd) volatile unsigned char sx_test_errors;
        __xdata __at(0x6fe) volatile unsigned char sx_test_stall;
        __xdata __at(0x6ff) volatile unsigned char sx_test_done;
        static const char sx_test_long[] =
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        #define SX_CHECK(condition) do { if (!(condition)) { ++sx_test_errors; } } while (0)
        void main(void)
        {
            sx_test_errors = 0;
            sx_test_stall = 0;
            /* C52 夹具不运行 STC 的定时器；只给轮询函数提供配置后的寄存器状态。 */
            EA = 0; ES = 0; IE2 = 0; SCON = 0x50; S2CON = 0x11; AUXR = 0x10; TR1 = 1;
            SX_CHECK(USART1_printf("U1:%s %d %u %04X %ld %% %c\r\n", "abc", -12, 65535u, 0x2au, 123456L, '!') == 34);
            SX_CHECK(EA == 0);
            EA = 1;
            SX_CHECK(USART2_printf("U2:[%8s] [%.3s] [%+06d] [%-4u]\n", "abc", "abcdef", 42, 7u) == 36);
            /* 锁定 SDCC 原库的已知差异：%8.3s 按未截断字符串长度补空格，实际输出只有 5 列。 */
            SX_CHECK(USART2_printf("[%8.3s]\n", "abcdef") == 8);
            SX_CHECK(EA == 1 && (S2CON & 1) != 0);
            SX_CHECK(USART1_printf("%s", sx_test_long) == 180);
            SX_CHECK(USART1_printf("") == 0 && USART2_printf("") == 0);
            SX_CHECK(USART1_printf((const char *)0) == -1);
            sx_printf_busy = 1;
            SX_CHECK(USART1_printf("BUSY") == -1 && USART2_printf("BUSY") == -1 && EA == 1);
            sx_printf_busy = 0;
            ES = 1;
            SX_CHECK(USART1_printf("IRQ") == -1);
            ES = 0; IE2 = 1;
            SX_CHECK(USART2_printf("IRQ") == -1);
            IE2 = 0; TR1 = 0;
            SX_CHECK(USART1_printf("CLOCK") == -1);
            TR1 = 1; AUXR = 0;
            SX_CHECK(USART2_printf("CLOCK") == -1);
            AUXR = 0x10; sx_test_stall = 1;
            SX_CHECK(USART2_printf("LOST") == -1);
            SX_CHECK(USART2_printf("DROP") == -1);
            sx_test_stall = 0; S2CON |= 2;
            SX_CHECK(USART2_printf("R") == 1 && EA == 1 && (S2CON & 1) != 0);
            sx_test_done = 0xa5;
            for (;;) { }
        }

        """;
}
