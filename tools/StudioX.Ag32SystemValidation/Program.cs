using System.Text;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is not [var toolsRoot, var packRoot, var outputRoot, var hostGcc])
{
    throw new ArgumentException("tools packs new-output native-gcc");
}
var output = Path.GetFullPath(outputRoot);
if (Directory.Exists(output))
{
    throw new IOException("Output must be new.");
}
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
        var initialVe = await File.ReadAllTextAsync(Path.Combine(root, "logic/pins.ve"));
        Check(initialVe.ReplaceLineEndings("\n") == "HSECLK 8\nSYSCLK 200\nBUSCLK 100\n",
            device.Id + " starts with exactly the three editable clock settings");
        var initialHeader = await File.ReadAllTextAsync(Path.Combine(root, Ag32SystemSupport.HeaderPath));
        Check(initialHeader.Contains("STUDIOX_CONFIGURE_PLL 1") && initialHeader.Contains("STUDIOX_HSE_HZ 8000000u") &&
            initialHeader.Contains("STUDIOX_SYSCLK_HZ 200000000u") && initialHeader.Contains("STUDIOX_BUSCLK_HZ 100000000u"),
            device.Id + " initial MCU clock metadata matches the VE");
        var main = Path.Combine(root, "src/main.c");
        Check(File.ReadAllText(main).Contains("StudioX_System.h") && !File.ReadAllText(main).Contains("CLK_CNTL"), device.Id + " new main contains no clock or peripheral implementation");
        var userCode = "#include \"StudioX_System.h\"\nint main(void) { GPIO_SetHigh(LED1_Port, LED1_Bit); GPIO_SetLow(LED1.Port, LED1.Bit); Delay_us(1); Delay_ms(1); Delay_1ms(); for (;;) {} }\n";
        await File.WriteAllTextAsync(main, userCode);
        var snapshot = await planner.ReadAsync(root);
        var plan = await planner.ApplyAsync(root, snapshot, [new("GPIO4_4", 2, "OUTPUT", "LED1")], new(8, 200, 100));
        var expectedMain = await File.ReadAllTextAsync(main);
        Check(expectedMain.StartsWith("#include \"StudioX_System.h\"\n", StringComparison.Ordinal) &&
            expectedMain.Contains("/* STUDIOX GENERATED BEGIN Includes */") && expectedMain.Contains("#include \"GPIO.h\"") &&
            expectedMain.Contains("/* STUDIOX GENERATED END Includes */") &&
            expectedMain.Contains("SX_GPIO_Init();\n    /* STUDIOX GENERATED END Init */") &&
            expectedMain.EndsWith(userCode[(userCode.IndexOf('{') + 1)..], StringComparison.Ordinal),
            device.Id + " adds bounded sections to an unmarked inline main and preserves its business statements");
        var vePath = Path.Combine(root, "logic/pins.ve");
        var header = Path.Combine(root, Ag32SystemSupport.HeaderPath);
        var source = Path.Combine(root, Ag32SystemSupport.SourcePath);
        var generated = await File.ReadAllTextAsync(header);
        Check(File.ReadAllText(vePath).Contains("GPIO4_4 PIN_2:OUTPUT #LED1") && plan.Snapshot.Assignments.Single().Name == "LED1", device.Id + " name persists as plain VE comment and reloads");
        Check(generated.Contains("#include \"alta.h\"") && generated.Contains("#define LED1_Port GPIO4") && generated.Contains("#define LED1_Bit GPIO_BIT4") && generated.Contains("#define LED1_Pin 2u"), device.Id + " all-peripheral umbrella and physical/internal pin distinction");
        Check(generated.Contains("STUDIOX_SYSCLK_HZ 200000000u") && generated.Contains("STUDIOX_BUSCLK_HZ 100000000u") && File.ReadAllText(main) == expectedMain, device.Id + " clock conversion updates system pair while preserving user main");
        Check((await sourceFiles.ReadAsync(root, Ag32SystemSupport.HeaderPath)).IsReadOnly && (await sourceFiles.ReadAsync(root, Ag32SystemSupport.SourcePath)).IsReadOnly, device.Id + " generated system pair opens read only");
        var build = await new BuildService(tools).ConfigureAsync(root);
        Check(build.Success, device.Id + " CMake configure succeeds: " + build.LogPath);
        var gcc = await tools.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
        var result = await runner.RunAsync(new(gcc.Tool("cmake"), ["--build", Path.Combine(root, ".build"), "--parallel", "4"], root,
            TimeSpan.FromMinutes(2), ToolsetEnvironment.Create(gcc), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        await File.WriteAllTextAsync(Path.Combine(root, "compile.log"), result.StandardOutput + result.StandardError);
        Check(result.Success && File.Exists(Path.Combine(root, ".build/firmware.elf")), device.Id + " actual RISC-V GCC links named GPIO, explicit init and both delay units");
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
            // 新工程的头文件索引异步建立，等待声明可见后再断言补全结果。
            var indexing = System.Diagnostics.Stopwatch.StartNew();
            bool hasDelays;
            do
            {
                completions = await intelligence.CompleteAsync("src/main.c", text, text.IndexOf("Delay_", StringComparison.Ordinal) + "Delay_".Length);
                hasDelays = new[] { "Delay_us", "Delay_ms" }.All(name => completions.Any(item => item.Label.TrimStart().StartsWith(name, StringComparison.Ordinal)));
                if (hasDelays)
                {
                    break;
                }
                await Task.Delay(100);
            } while (indexing.Elapsed < TimeSpan.FromSeconds(5));
            Check(hasDelays, "actual clangd completes generated delay functions: " + string.Join(", ", completions.Select(item => item.Label)) + "\n" + string.Join("\n", intelligence.DrainLog()));
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
        Check(generated.Contains("STATUS_LED_Port") && !generated.Contains("LED1_Port") && generated.Contains("STUDIOX_SYSCLK_HZ 160000000u") && File.ReadAllText(main) == expectedMain, "text rename removes stale aliases and updates clocks without editing main");
        var renamed = await planner.ReadAsync(root);
        await planner.ApplyAsync(root, renamed, [new("GPIO4_4", 2)], renamed.Clocks);
        Check(!File.ReadAllText(header).Contains("STATUS_LED") && !File.ReadAllText(vePath).Contains("#STATUS_LED"), "clearing name removes VE comment and C aliases together");
        var finalSnapshot = await planner.ReadAsync(root);
        await planner.ApplyAsync(root, finalSnapshot, [new("GPIO4_4", 2, "OUTPUT", "LED1")], new(8, 200, 100));
        var legacy = "#include \"StudioX_System.h\"\r\n#include \"GPIO.h\"\r\nint main(void)\r\n{\r\n    SX_GPIO_Init();\r\n    /* 用户业务 */\r\n    while (1) { }\r\n}\r\n";
        var utf8Bom = new UTF8Encoding(true);
        await File.WriteAllTextAsync(main, legacy, utf8Bom);
        async Task<Ag32PinPlanResult> RegenerateMain()
        {
            var current = await planner.ReadAsync(root);
            return await planner.ApplyAsync(root, current, current.Assignments, current.Clocks);
        }
        await RegenerateMain();
        var markedMain = await File.ReadAllTextAsync(main);
        Check(markedMain.Split("SX_GPIO_Init();").Length == 2 && markedMain.Contains("SX_GPIO_Init();\r\n    /* STUDIOX GENERATED END Init */") &&
            markedMain.Contains("#include \"GPIO.h\"\r\n/* STUDIOX GENERATED END Includes */") && markedMain.EndsWith(legacy[legacy.IndexOf("    /* 用户业务", StringComparison.Ordinal)..], StringComparison.Ordinal),
            "legacy generated lines move into marked sections while application text and CRLF survive");
        var userEdit = markedMain.Replace("/* USER CODE BEGIN BeforeInit */", "/* USER CODE BEGIN BeforeInit */\r\n    /* 应用初始化 */");
        await File.WriteAllTextAsync(main, userEdit.Replace("SX_GPIO_Init();", "outdated();"), utf8Bom);
        await RegenerateMain();
        var restored = await File.ReadAllBytesAsync(main);
        Check(restored.SequenceEqual(utf8Bom.GetPreamble().Concat(utf8Bom.GetBytes(userEdit))) &&
            (await RegenerateMain()).UpdatedFiles.Length == 0, "regeneration replaces generated text only and the next save is byte-identical");
        string DeleteSection(string input, string name) => System.Text.RegularExpressions.Regex.Replace(input,
            $@"(?ms)^[ \t]*/\* STUDIOX GENERATED BEGIN {name} \*/.*?^[ \t]*/\* STUDIOX GENERATED END {name} \*/\r?\n", "");
        var noIncludes = DeleteSection(userEdit, "Includes");
        var noSections = DeleteSection(noIncludes, "Init");
        foreach (var edited in new[]
        {
            noIncludes, DeleteSection(userEdit, "Init"), noSections,
            userEdit.Replace("/* STUDIOX GENERATED END Init */", "/* keep this note */"),
            userEdit.Replace("/* STUDIOX GENERATED BEGIN Includes */", ""),
            System.Text.RegularExpressions.Regex.Replace(userEdit, @"/\* STUDIOX GENERATED (?:BEGIN|END) (?:Includes|Init) \*/", "")
        })
        {
            await File.WriteAllTextAsync(main, edited, utf8Bom);
            await RegenerateMain();
            var repaired = await File.ReadAllTextAsync(main);
            var call = repaired.IndexOf("SX_GPIO_Init();", StringComparison.Ordinal);
            Check(repaired.Split("#include \"GPIO.h\"").Length == 2 && repaired.Split("SX_GPIO_Init();").Length == 2 &&
                repaired.Contains("/* STUDIOX GENERATED BEGIN Includes */") && repaired.Contains("/* STUDIOX GENERATED END Init */") &&
                repaired.IndexOf("/* 应用初始化 */", StringComparison.Ordinal) >= 0 &&
                repaired.IndexOf("/* USER CODE END BeforeInit */", StringComparison.Ordinal) < call &&
                call < repaired.IndexOf("/* USER CODE BEGIN AfterInit */", StringComparison.Ordinal) &&
                repaired.Contains("    /* 用户业务 */\r\n    while (1) { }") &&
                (!edited.Contains("/* keep this note */") || repaired.Contains("/* keep this note */")) &&
                (await RegenerateMain()).UpdatedFiles.Length == 0, "deleted main regions recover without duplicate calls or loss of user code");
        }
        var ambiguous = userEdit.Replace("/* USER CODE END BeforeInit */", "/* STUDIOX GENERATED BEGIN Init */\r\n    /* USER CODE END BeforeInit */");
        await File.WriteAllTextAsync(main, ambiguous, utf8Bom);
        try
        {
            await RegenerateMain();
            throw new InvalidOperationException("Overlapping section boundaries accepted");
        }
        catch (StudioXException error) when (error.Code == "AG32_MAIN_INIT")
        {
            Check(await File.ReadAllTextAsync(main) == ambiguous, "ambiguous duplicate boundaries leave user content intact");
        }
        await File.WriteAllBytesAsync(main, restored);
    }
}
if (nativeHeader is null || nativeSource is null)
{
    throw new InvalidOperationException("No verified devices");
}
var native = Path.Combine(output, "native");
Directory.CreateDirectory(native);
File.Copy(nativeHeader, Path.Combine(native, "StudioX_System.h"));
File.Copy(nativeSource, Path.Combine(native, "StudioX_System.c"));
File.Copy(Path.Combine(Path.GetDirectoryName(nativeHeader)!, "StudioX_Board.h"), Path.Combine(native, "StudioX_Board.h"));
foreach (var file in new[] { "GPIO.c", "GPIO.h" })

{
    File.Copy(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(nativeHeader)!, "../../src", file)), Path.Combine(native, file));
}
foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "native")))
{
    File.Copy(file, Path.Combine(native, Path.GetFileName(file)));
}
var compiled = await runner.RunAsync(new(Path.GetFullPath(hostGcc), ["-std=gnu17", "-O2", "-I.", "StudioX_System.c", "GPIO.c", "clock_checks.c", "-o", "clock_checks.exe"], native, TimeSpan.FromSeconds(30)));
await File.WriteAllTextAsync(Path.Combine(native, "compile.log"), compiled.StandardOutput + compiled.StandardError);
Check(compiled.Success, "native clock/register fixture compiles generated source");
var nativeRun = await runner.RunAsync(new(Path.Combine(native, "clock_checks.exe"), [], native, TimeSpan.FromSeconds(15)));
await File.WriteAllTextAsync(Path.Combine(native, "result.txt"), nativeRun.StandardOutput + nativeRun.StandardError);
Check(nativeRun.Success, "generated C executes delay rounding, rollover, zero, large-ms, live clock, and startup timeout checks: " + nativeRun.StandardOutput);
await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
Console.WriteLine($"PASS {checks.Count}; compiler and register simulation only; no hardware or Supra execution.");
