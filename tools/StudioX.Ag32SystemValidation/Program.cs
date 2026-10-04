using System.Text;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is not [var toolsRoot, var packRoot, var outputRoot, var hostGcc]) { throw new ArgumentException("tools packs new-output native-gcc"); }
var output = Path.GetFullPath(outputRoot);
if (Directory.Exists(output)) { throw new IOException("Output must be new."); }
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool value, string text)
{
    if (!value)
    {
        throw new InvalidOperationException(text);
    }
    checks.Add(text);
    Console.WriteLine("PASS " + text);
}
var tools = new ToolsetCatalog(Path.GetFullPath(toolsRoot));
var packs = new PackRepository(Path.Combine(output, "packs"));
var planner = new Ag32PinPlanningService(tools);
var sourceFiles = new ProjectFileService();
var runner = new ProcessRunner();
string? nativeHeader = null;
string? nativeSource = null;
foreach (var archive in Directory.GetFiles(Path.GetFullPath(packRoot), "*.mcupack").Order())
{
    var pack = await packs.ImportAsync(archive);
    foreach (var device in pack.Manifest.Devices.Where(Ag32DeviceCatalog.Matches))
    {
        var root = Path.Combine(output, device.Id);
        await new ProjectService().CreateAsync(pack, device.Id, "minimal", "system_test", root);
        var main = Path.Combine(root, "src/main.c");
        Check(File.ReadAllText(main).Contains("StudioX_System.h") && !File.ReadAllText(main).Contains("CLK_CNTL"), device.Id + " new main contains no clock or peripheral implementation");
        var userCode = "#include \"StudioX_System.h\"\nint main(void) { GPIO_SetHigh(LED1_Port, LED1_Bit); GPIO_SetLow(LED1.Port, LED1.Bit); Delay_us(1); Delay_ms(1); Delay_1ms(); for (;;) {} }\n";
        await File.WriteAllTextAsync(main, userCode);
        var snapshot = await planner.ReadAsync(root);
        var plan = await planner.ApplyAsync(root, snapshot, [new("GPIO4_4", 2, "OUTPUT", "LED1")], new(8, 200, 100));
        var vePath = Path.Combine(root, "logic/pins.ve");
        var header = Path.Combine(root, Ag32SystemSupport.HeaderPath);
        var source = Path.Combine(root, Ag32SystemSupport.SourcePath);
        var generated = await File.ReadAllTextAsync(header);
        Check(File.ReadAllText(vePath).Contains("GPIO4_4 PIN_2:OUTPUT #LED1") && plan.Snapshot.Assignments.Single().Name == "LED1", device.Id + " name persists as plain VE comment and reloads");
        Check(generated.Contains("#include \"alta.h\"") && generated.Contains("#define LED1_Port GPIO4") && generated.Contains("#define LED1_Bit GPIO_BIT4") && generated.Contains("#define LED1_Pin 2u"), device.Id + " all-peripheral umbrella and physical/internal pin distinction");
        Check(generated.Contains("STUDIOX_SYSCLK_HZ 200000000u") && generated.Contains("STUDIOX_BUSCLK_HZ 100000000u") && File.ReadAllText(main) == userCode, device.Id + " clock conversion updates system pair while preserving user main");
        Check((await sourceFiles.ReadAsync(root, Ag32SystemSupport.HeaderPath)).IsReadOnly && (await sourceFiles.ReadAsync(root, Ag32SystemSupport.SourcePath)).IsReadOnly, device.Id + " generated system pair opens read only");
        var build = await new BuildService(tools).ConfigureAsync(root);
        Check(build.Success, device.Id + " CMake configure succeeds: " + build.LogPath);
        var gcc = await tools.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
        var result = await runner.RunAsync(new(gcc.Tool("cmake"), ["--build", Path.Combine(root, ".build"), "--parallel", "4"], root,
            TimeSpan.FromMinutes(2), ToolsetEnvironment.Create(gcc), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        await File.WriteAllTextAsync(Path.Combine(root, "compile.log"), result.StandardOutput + result.StandardError);
        Check(result.Success && File.Exists(Path.Combine(root, ".build/firmware.elf")), device.Id + " actual RISC-V GCC links named GPIO, automatic init and both delay units");
        nativeHeader ??= header;
        nativeSource ??= source;
        if (device.Id != "AG32VF303CCT6")
        {
            continue;
        }
        await using (var intelligence = new CodeIntelligenceService(Path.GetDirectoryName(Path.GetFullPath(toolsRoot))!, Path.Combine(output, "language-data")))
        {
            await intelligence.StartAsync(root);
            var text = "#include \"StudioX_System.h\"\nvoid sample(void) { LED1. }\n";
            var completions = await intelligence.CompleteAsync("src/main.c", text, text.IndexOf("LED1.", StringComparison.Ordinal) + 5);
            Check(completions.Any(item => item.Label.Trim() == "Port") && completions.Any(item => item.Label.Trim() == "Bit"),
                "actual clangd completes named pin object members through system header: " + string.Join(", ", completions.Select(item => item.Label)) + "\n" + string.Join("\n", intelligence.DrainLog()));
            text = "#include \"StudioX_System.h\"\nvoid sample(void) { Delay_ }\n";
            completions = await intelligence.CompleteAsync("src/main.c", text, text.IndexOf("Delay_", StringComparison.Ordinal) + 6);
            Check(completions.Any(item => item.Label.TrimStart().StartsWith("Delay_us", StringComparison.Ordinal)) && completions.Any(item => item.Label.TrimStart().StartsWith("Delay_ms", StringComparison.Ordinal)), "actual clangd completes generated delay functions: " + string.Join(", ", completions.Select(item => item.Label)));
        }
        var originalVe = await File.ReadAllBytesAsync(vePath);
        var originalHeader = await File.ReadAllBytesAsync(header);
        async Task Reject(Ag32PinAssignment[] assignments, string code)
        {
            var fresh = await planner.ReadAsync(root);
            try
            {
                await planner.ApplyAsync(root, fresh, assignments, fresh.Clocks);
                throw new InvalidOperationException("Not rejected " + code);
            }
            catch (StudioXException ex) when (ex.Code == code)
            {
                Check(originalVe.SequenceEqual(File.ReadAllBytes(vePath)) && originalHeader.SequenceEqual(File.ReadAllBytes(header)), "invalid naming rejected without partial VE/system writes: " + assignments[0].Name);
            }
        }
        await Reject([new("GPIO4_4", 2, "OUTPUT", "bad-name")], "AG32_PIN_PLAN_CONFLICT");
        await Reject([new("GPIO4_4", 2, "OUTPUT", "for")], "AG32_PIN_PLAN_CONFLICT");
        await Reject([new("GPIO4_4", 2, "OUTPUT", "LED1"), new("GPIO4_5", 21, "INPUT", "LED1")], "AG32_PIN_PLAN_CONFLICT");
        await Reject([new("GPIO4_4", 2, "OUTPUT", "LED1"), new("GPIO4_5", 21, "INPUT", "LED1_Port")], "AG32_PIN_PLAN_CONFLICT");
        await File.AppendAllTextAsync(header, "\n/* user's external edit */\n");
        var changedHeader = await File.ReadAllBytesAsync(header);
        try
        {
            await planner.ApplyAsync(root, plan.Snapshot, [new("GPIO4_4", 2, "OUTPUT", "LED2")], plan.Snapshot.Clocks);
            throw new InvalidOperationException("External edit overwritten");
        }
        catch (StudioXException ex) when (ex.Code == "AG32_SYSTEM_MODIFIED")
        {
            Check(File.ReadAllBytes(header).SequenceEqual(changedHeader) && File.ReadAllBytes(vePath).SequenceEqual(originalVe), "external generated-file edit preserved and VE transaction rejected");
        }
        await File.WriteAllBytesAsync(header, originalHeader);
        // 模拟用户直接编辑 VE；下一次配置必须重新生成名称与频率。
        await File.WriteAllTextAsync(vePath, "HSECLK 8\nSYSCLK 160\nBUSCLK 80\nGPIO4_4 PIN_2:OUTPUT #STATUS_LED\n");
        Check((await new BuildService(tools).ConfigureAsync(root)).Success, "text VE changes regenerate before MCU compilation");
        generated = await File.ReadAllTextAsync(header);
        Check(generated.Contains("STATUS_LED_Port") && !generated.Contains("LED1_Port") && generated.Contains("STUDIOX_SYSCLK_HZ 160000000u") && File.ReadAllText(main) == userCode, "text rename removes stale aliases and updates clocks without editing main");
        var renamed = await planner.ReadAsync(root);
        await planner.ApplyAsync(root, renamed, [new("GPIO4_4", 2)], renamed.Clocks);
        Check(!File.ReadAllText(header).Contains("STATUS_LED") && !File.ReadAllText(vePath).Contains("#STATUS_LED"), "clearing name removes VE comment and C aliases together");
        var finalSnapshot = await planner.ReadAsync(root);
        await planner.ApplyAsync(root, finalSnapshot, [new("GPIO4_4", 2, "OUTPUT", "LED1")], new(8, 200, 100));
    }
}
if (nativeHeader is null || nativeSource is null) { throw new InvalidOperationException("No verified devices"); }
var native = Path.Combine(output, "native");
Directory.CreateDirectory(native);
File.Copy(nativeHeader, Path.Combine(native, "StudioX_System.h"));
File.Copy(nativeSource, Path.Combine(native, "StudioX_System.c"));
File.Copy(Path.Combine(Path.GetDirectoryName(nativeHeader)!, "StudioX_Board.h"), Path.Combine(native, "StudioX_Board.h"));
foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "native"))) { File.Copy(file, Path.Combine(native, Path.GetFileName(file))); }
var compiled = await runner.RunAsync(new(Path.GetFullPath(hostGcc), ["-std=gnu17", "-O2", "-I.", "StudioX_System.c", "clock_checks.c", "-o", "clock_checks.exe"], native, TimeSpan.FromSeconds(30)));
await File.WriteAllTextAsync(Path.Combine(native, "compile.log"), compiled.StandardOutput + compiled.StandardError);
Check(compiled.Success, "native clock/register fixture compiles generated source");
var nativeRun = await runner.RunAsync(new(Path.Combine(native, "clock_checks.exe"), [], native, TimeSpan.FromSeconds(15)));
await File.WriteAllTextAsync(Path.Combine(native, "result.txt"), nativeRun.StandardOutput + nativeRun.StandardError);
Check(nativeRun.Success, "generated C executes delay rounding, rollover, zero, large-ms, live clock, and startup timeout checks: " + nativeRun.StandardOutput);
await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
Console.WriteLine($"PASS {checks.Count}; compiler and register simulation only; no hardware or Supra execution.");
