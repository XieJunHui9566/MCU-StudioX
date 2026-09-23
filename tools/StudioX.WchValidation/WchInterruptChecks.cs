using System.Text;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;

internal static class WchInterruptChecks
{
    // 只生成目标文件并反汇编，不生成可下载映像、不调用 GDB 或 OpenOCD。
    public static async Task RunAsync(string runtime, string output, string architecture = "rv32imac_xw")
    {
        if (architecture is not ("rv32imac_xw" or "rv32imc_zba_zbb_zbc_zbs_xw")) throw new ArgumentException("Unsupported WCH ISA.");
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Use a new output directory.");
        var tools = await new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"))
            .ResolveAsync("wch.riscv", "1.0.0", "wch-gcc-12.2.0-v1.4");
        Directory.CreateDirectory(root);
        const string source = """
            #ifdef __cplusplus
            extern "C" {
            #endif
            extern unsigned work(unsigned);
            volatile unsigned counter;
            void TIM2_IRQHandler(void) __attribute__((interrupt("WCH-Interrupt-fast")));
            void TIM3_IRQHandler(void) __attribute__((interrupt()));
            void TIM2_IRQHandler(void) { counter = work(counter); }
            void TIM3_IRQHandler(void) { counter = work(counter); }
            void ordinary_handler(void) { counter = work(counter); }
            #ifdef __cplusplus
            }
            #endif
            """;
        var log = new StringBuilder();
        foreach (var (extension, role) in new[] { ("c", "gcc"), ("cpp", "gxx") })
        {
            var file = Path.Combine(root, "interrupt-check." + extension);
            await File.WriteAllTextAsync(file, source);
            foreach (var optimization in new[] { "O0", "Og", "O1", "O2", "O3", "Os" })
            {
                var name = extension + "-" + optimization;
                var obj = Path.Combine(root, name + ".o");
                await Run(role, ["-march=" + architecture, "-mabi=ilp32", "-msmall-data-limit=8", "-msave-restore",
                    "-" + optimization, "-g3", "-ffunction-sections", "-Wall", "-Wextra", "-Werror", "-c", file, "-o", obj]);
                var assembly = new StringBuilder();
                foreach (var function in new[] { "TIM2_IRQHandler", "TIM3_IRQHandler", "ordinary_handler" })
                {
                    var text = await Run("objdump", ["-dr", "--disassemble=" + function, obj]);
                    if (!text.Contains("<" + function + ">:", StringComparison.Ordinal))
                        throw new InvalidOperationException("Missing unmangled symbol: " + function);
                    var mret = Regex.IsMatch(text, @"\bmret\b");
                    if (mret != (function != "ordinary_handler"))
                        throw new InvalidOperationException("Wrong interrupt return: " + name + " " + function);
                    assembly.AppendLine(text);
                }
                await File.WriteAllTextAsync(Path.Combine(root, name + ".disassembly.txt"), assembly.ToString());
                var line = "PASS " + name + ": fast/software ISR use mret; ordinary function does not; C symbols preserved.";
                Console.WriteLine(line); log.AppendLine(line);
            }
        }
        log.AppendLine("ISA: " + architecture + ". Object-code checks only; no hardware access or interrupt nesting runtime validation.");
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), log.ToString());

        async Task<string> Run(string role, string[] arguments)
        {
            var result = await new ProcessRunner().RunAsync(new(tools.Tool(role), arguments, root, TimeSpan.FromSeconds(30),
                ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            if (!result.Success) throw new InvalidOperationException(result.StandardOutput + result.StandardError);
            return result.StandardOutput;
        }
    }
}
