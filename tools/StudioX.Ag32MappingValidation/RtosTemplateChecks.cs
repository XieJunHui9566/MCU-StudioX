using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class RtosTemplateChecks
{
    internal static async Task RunAsync(string toolsets, string directory, string archives)
    {
        var output = Path.GetFullPath(directory);
        if (Directory.Exists(output)) throw new IOException("Use a new validation directory.");
        Directory.CreateDirectory(output);
        var repository = new PackRepository(Path.Combine(output, "packs"));
        var catalog = new ToolsetCatalog(Path.GetFullPath(toolsets));
        var compiler = await catalog.ResolveAsync("agm.agrv", "1.0.0", "agrv-gcc-11.1.0");
        var runner = new ProcessRunner();
        var checks = new List<string>();
        void Check(bool success, string text)
        {
            if (!success) throw new InvalidOperationException(text);
            checks.Add(text); Console.WriteLine("PASS " + text);
        }
        foreach (var archive in Directory.GetFiles(Path.GetFullPath(archives), "*.mcupack"))
        {
            var pack = await repository.ImportAsync(archive);
            foreach (var device in pack.Manifest.Devices)
            foreach (var mixed in new[] { false, true })
            {
                var template = mixed ? "freertos-mcu-with-logic" : "freertos-mcu";
                var root = Path.Combine(output, "中文工程 with spaces", device.Id + "-" + template);
                Check(device.Templates.Count(item => item.Id.StartsWith("freertos-", StringComparison.Ordinal)) == 1,
                    device.Id + " provides one FreeRTOS template");
                var project = await new ProjectService().CreateAsync(pack, device.Id, "freertos-mcu", "rtos_demo", root, enableAg32Logic: mixed);
                Check((project.Logic is not null) == mixed && project.PinMapping is not null,
                    device.Id + " " + template + " special mode follows the creation checkbox only");
                var source = await File.ReadAllTextAsync(Path.Combine(root, "src/main.c"));
                var cmake = await File.ReadAllTextAsync(Path.Combine(root, "device/CMakeLists.txt"));
                Check(source.Contains("app_logic_value") == mixed && cmake.Contains("STUDIOX_AG32_LOGIC_LOOPBACK") == mixed &&
                    File.Exists(Path.Combine(root, "logic/user_logic.v")) == mixed,
                    device.Id + " " + template + " only adds logic entry, macro and Verilog when requested");
                var build = new BuildService(catalog);
                var configured = await build.ConfigureAsync(root);
                await File.WriteAllTextAsync(Path.Combine(root, "configure.log"), configured.Log);
                Check(configured.Success, device.Id + " " + template + " configures with generated system support");
                var result = await runner.RunAsync(new(compiler.Tool("ninja"), ["-C", ".build"], root,
                    TimeSpan.FromMinutes(3), ToolsetEnvironment.Create(compiler), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
                await File.WriteAllTextAsync(Path.Combine(root, "compile.log"), result.StandardOutput + result.StandardError);
                Check(result.Success && !Regex.IsMatch(result.StandardOutput + result.StandardError, @"(?i)warning:"),
                    device.Id + " " + template + " real GCC and linker succeed without warnings");
                var map = await File.ReadAllTextAsync(Path.Combine(root, ".build/firmware.map"));
                Check(map.Contains("portASM.S.obj") && map.Contains("heap_4.c.obj") && map.Contains("StudioX_FreeRtosInterruptHandler") &&
                    map.Contains("vPortSetupTimerInterrupt") && map.Contains("StudioX_BeforeMain"),
                    device.Id + " " + template + " links scheduler, heap, timer and system constructor");
                Check(Regex.IsMatch(map, @"\.text\.trap_entry\s+0x[0-9a-f]+\s+0x[0-9a-f]+[^\r\n]+portASM\.S\.obj") &&
                    Regex.IsMatch(map, @"\.text\.vPortSetupTimerInterrupt\s+0x0*800[0-9a-f]+\s+0x[0-9a-f]+[^\r\n]+StudioX_System\.c\.obj"),
                    device.Id + " " + template + " resolves the RTOS trap and actual-clock timer instead of weak fallbacks");
                Check(File.Exists(Path.Combine(root, "device/vendor/licenses/FreeRTOS-LICENSE.md")),
                    device.Id + " " + template + " keeps the FreeRTOS license in generated projects");
                if (device.Id == "AG32VF303CCT6")
                {
                    var assembly = await runner.RunAsync(new(compiler.Tool("objdump"), ["-d", "-M", "numeric", ".build/firmware.elf"], root,
                        TimeSpan.FromMinutes(1), ToolsetEnvironment.Create(compiler), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
                    await File.WriteAllTextAsync(Path.Combine(root, "firmware-disassembly.txt"), assembly.StandardOutput + assembly.StandardError);
                    Check(assembly.Success && assembly.StandardOutput.Contains("fsw\tf0,4(x2)") &&
                        assembly.StandardOutput.Contains("flw\tf0,4(x2)") && assembly.StandardOutput.Contains("frcsr") &&
                        assembly.StandardOutput.Contains("fscsr"), template + " compiled trap preserves f0 outside mepc and saves floating control state");
                    if (!mixed)
                    {
                        // 仅此隔离夹具显式选 PIN2；交付模板不假定开发板接线。
                        await File.WriteAllTextAsync(Path.Combine(root, "logic/pins.ve"), "HSECLK 8\nSYSCLK 200\nBUSCLK 100\nGPIO4_4 PIN_2:OUTPUT #LED1\n");
                    }
                    var joint = await build.BuildAsync(root);
                    await File.WriteAllTextAsync(Path.Combine(root, "joint-build.log"), joint.Log);
                    Check(joint.Success, template + " actual MCU + mapping/native FPGA combined build");
                }
            }
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, checks, hardwareAccessed = false });
    }
}
