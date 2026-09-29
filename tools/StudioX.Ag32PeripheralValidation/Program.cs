using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Length < 3) throw new ArgumentException("tools-root pack-root new-output [--route]");
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException(description);
    checks.Add(description); Console.WriteLine("PASS " + description);
    File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
}
var tools = new ToolsetCatalog(Path.GetFullPath(args[0]));
var packs = new PackRepository(Path.Combine(output, "packs"));
var planner = new Ag32PinPlanService(tools);
var runner = new ProcessRunner();
foreach (var file in Directory.GetFiles(Path.GetFullPath(args[1]), "*.mcupack"))
{
    var pack = await packs.ImportAsync(file);
    foreach (var device in pack.Manifest.Devices.Where(Ag32DeviceCatalog.Matches))
    {
        var root = Path.Combine(output, device.Id);
        await new ProjectService().CreateAsync(pack, device.Id, "minimal", "peripheral_test", root);
        var snapshot = await planner.ReadAsync(root);
        var options = new Ag32AnalogSettings(true, 1u, true, true, true);
        var configured = await planner.ApplyAsync(root, snapshot, [new("UART0_UARTTXD", 2)], new(8, 100, 50), options);
        Check(configured.Snapshot.Analog == options && (await planner.ReadAsync(root)).Analog == options, device.Id + " analog settings round trip");
        var systemCode = File.ReadAllText(Path.Combine(root, Ag32SystemSupport.SourcePath));
        Check(systemCode.Contains("GPIO_SetHardwareMode") && systemCode.Contains("SYS_EnableAPBClock(APB_MASK_UART0)"), device.Id + " mapped peripheral enables module clock and hardware mode");
        var reserved = Ag32PeripheralSupport.ReservedPins(device.Id, options);
        foreach (var bad in new[] { new Ag32AnalogSettings(true, 0x10000), options })
        {
            var before = File.ReadAllBytes(Path.Combine(root, "logic/pins.ve"));
            try
            {
                await planner.ApplyAsync(root, configured.Snapshot,
                    bad.AdcChannels == 0x10000 ? [] : [new("GPIO4_4", reserved[0].Pin)], configured.Snapshot.Clocks, bad);
                throw new InvalidOperationException("Invalid analog config accepted");
            }
            catch (StudioXException error) when (error.Code is "AG32_ANALOG_CHANNEL" or "AG32_ANALOG_PIN")
            {
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "logic/pins.ve"))), device.Id + " rejects " + error.Code + " without writes");
            }
        }
        var gcc = await tools.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
        await File.WriteAllTextAsync(Path.Combine(root, "src/main.c"), "#include \"StudioX_System.h\"\nint main(void) { for (;;) {} }\n");
        // 从 GCC 实际解析的声明取清单，覆盖 typedef 返回值、指针、内联和小写 API。
        var inventory = await runner.RunAsync(new(gcc.Tool("gcc"), [.. device.CpuFlags,
            "-Idevice/studiox", "-Idevice/studiox/vendor", "-Idevice/sdk/include", "-Idevice/sdk/startup",
            "-aux-info", "api.aux", "-fsyntax-only", "src/main.c"], root, TimeSpan.FromSeconds(30),
            ToolsetEnvironment.Create(gcc), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        File.WriteAllText(Path.Combine(root, "inventory.log"), inventory.StandardOutput + inventory.StandardError);
        Check(inventory.Success, device.Id + " GCC parses all public SDK and analog headers");
        var symbols = Regex.Matches(File.ReadAllText(Path.Combine(root, "api.aux")),
            @"(?m)^/\* device/(?:sdk/include|studiox/vendor)/[^:\r\n]+:[0-9]+:[A-Z]+ \*/ (?:extern|static) [^(;\r\n]*?\b([A-Za-z_]\w*) \(")
            .Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
        Check(symbols.Length > 500, device.Id + " compiler-derived complete API inventory " + symbols.Length);
        File.WriteAllLines(Path.Combine(root, "api-symbols.txt"), symbols);
        var source = "#include \"StudioX_System.h\"\n" +
            "void (* volatile calls[])(void) = {\n" + string.Join(",\n", symbols.Select(name => "(void (*)(void))" + name)) +
            ", (void (*)(void))StudioX_ADC_Read, (void (*)(void))StudioX_DAC_Write, (void (*)(void))CMP_Enable1, (void (*)(void))ADC_StartDma, (void (*)(void))DAC_EnableDma };\n" +
            "int main(void) { for (;;) { (void) calls[0]; } }\n";
        await File.WriteAllTextAsync(Path.Combine(root, "src/main.c"), source);
        var build = await new BuildService(tools).ConfigureAsync(root);
        Check(build.Success, device.Id + " configure: " + build.LogPath);
        var compiled = await runner.RunAsync(new(gcc.Tool("cmake"), ["--build", Path.Combine(root, ".build"), "--parallel", "4"], root,
            TimeSpan.FromMinutes(3), ToolsetEnvironment.Create(gcc), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        File.WriteAllText(Path.Combine(root, "compile.log"), compiled.StandardOutput + compiled.StandardError);
        Check(compiled.Success, device.Id + " all vendor API references link: " + Path.Combine(root, "compile.log"));
        if (args.Contains("--route-all") && device.Id != "AG32VF303CCT6")
        {
            var mapping = new Ag32PinMappingBuildService(tools);
            var result = await mapping.BuildAsync(root, new Progress<string>(Console.WriteLine));
            Check(result.Success, device.Id + " analog IP actual place/route: " + result.LogPath);
            _ = await mapping.ValidateBuiltAsync(root);
            Check(true, device.Id + " analog hard macro locations and receipt validate");
        }
        if (device.Id == "AG32VF303CCT6" && args.Contains("--route"))
        {
            var mapping = new Ag32PinMappingBuildService(tools);
            var result = await mapping.BuildAsync(root, new Progress<string>(Console.WriteLine));
            Check(result.Success, "Actual analog IP place/route: " + result.LogPath);
            _ = await mapping.ValidateBuiltAsync(root);
            Check(true, "Analog image receipt validates for current configuration");
            var changed = await planner.ApplyAsync(root, await planner.ReadAsync(root), [], configured.Snapshot.Clocks, options);
            try { await mapping.ValidateBuiltAsync(root); throw new InvalidOperationException("Stale image accepted"); }
            catch (StudioXException ex) when (ex.Code == "AG32_MAPPING_STALE") { Check(true, "Changing analog plan invalidates the existing image"); }
            var analogOnly = await mapping.BuildAsync(root, new Progress<string>(Console.WriteLine));
            Check(analogOnly.Success, "Analog-only image with zero GPIO mappings routes: " + analogOnly.LogPath);
            _ = await mapping.ValidateBuiltAsync(root);
            Check(true, "Analog-only image receipt and fixed converter locations validate");
            var snapshot2 = await planner.ReadAsync(root);
            await planner.ApplyAsync(root, snapshot2, [], snapshot2.Clocks, new Ag32AnalogSettings());
            Check(!File.ReadAllText(Path.Combine(root, "logic/pins.ve")).Contains(Ag32PeripheralSupport.Marker), "Disabling analog removes configuration marker");
            var header = Path.Combine(root, "device/sdk/include/uart.h");
            var original = File.ReadAllBytes(header);
            await File.AppendAllTextAsync(header, "\n/* external change */\n");
            try { await new BuildService(tools).ConfigureAsync(root); throw new InvalidOperationException("Modified SDK accepted"); }
            catch (StudioXException error) when (error.Code == "AG32_PERIPHERAL_SDK")
            { Check(true, "Modified SDK is rejected before driver replacement"); }
            File.WriteAllBytes(header, original);
            var enabledAgain = await planner.ReadAsync(root);
            await planner.ApplyAsync(root, enabledAgain, [], enabledAgain.Clocks, options);
            // C++ 用户同样只包含统一入口。
            var cpp = await runner.RunAsync(new(gcc.Tool("gxx"), [.. device.CpuFlags, "-std=gnu++17", "-fsyntax-only",
                "-Idevice/studiox", "-Idevice/studiox/vendor", "-Idevice/sdk/include", "-Idevice/sdk/startup", "src/main.c"], root,
                TimeSpan.FromSeconds(30), ToolsetEnvironment.Create(gcc), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            File.WriteAllText(Path.Combine(root, "cpp.log"), cpp.StandardOutput + cpp.StandardError);
            Check(cpp.Success, "C++ compilation of all API references: " + Path.Combine(root, "cpp.log"));
            File.Move(Path.Combine(root, "src/main.c"), Path.Combine(root, "src/main.cpp"));
            var userCmake = Path.Combine(root, "CMakeLists.txt");
            File.WriteAllText(userCmake, File.ReadAllText(userCmake).Replace("src/main.c", "src/main.cpp", StringComparison.Ordinal));
            Check((await new BuildService(tools).ConfigureAsync(root)).Success, "C++ project configures through normal IDE build service");
            var cppLink = await runner.RunAsync(new(gcc.Tool("cmake"), ["--build", Path.Combine(root, ".build"), "--parallel", "4"], root,
                TimeSpan.FromMinutes(3), ToolsetEnvironment.Create(gcc), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            File.WriteAllText(Path.Combine(root, "cpp-link.log"), cppLink.StandardOutput + cppLink.StandardError);
            Check(cppLink.Success, "C++ project links the complete API inventory with C drivers");
            if (Array.IndexOf(args, "--host-gcc") is var hostIndex && hostIndex >= 0)
            {
                await AnalogRegisterChecks.RunAsync(root, Path.GetFullPath(args[hostIndex + 1]));
                Check(true, "Generated ADC/DAC helpers pass host register and timeout checks");
            }
        }
    }
}
Console.WriteLine($"Completed {checks.Count} checks; offline only, no hardware connection.");
