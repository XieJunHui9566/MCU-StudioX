using System.Security.Cryptography;
using System.Text;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Length < 3) { throw new ArgumentException("Expected toolset root, a new output directory, and the verified AGM pack manifest."); }
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) { throw new IOException("Validation output must be new."); }
Directory.CreateDirectory(output);
var pack = await JsonStore.ReadAsync<PackManifest>(Path.GetFullPath(args[2]));
var device = pack.Devices.FirstOrDefault(item => item.Id == "AG32VF303CCT6") ?? pack.Devices.First();
var root = Path.Combine(output, "中文引脚工程 with spaces");
Directory.CreateDirectory(Path.Combine(root, ".studiox"));
Directory.CreateDirectory(Path.Combine(root, "device"));
Directory.CreateDirectory(Path.Combine(root, "logic"));
await JsonStore.WriteAsync(Path.Combine(root, "device/manifest.json"), pack);
var project = new ProjectManifest(1, "pin_plan_test", pack.Id, pack.Version, "fixture", device.Id, "minimal",
    device.ToolsetId, device.ToolsetVersion, device.CompilerId, PinMapping: new(Ag32DeviceCatalog.Require(device.Id).TargetDevice));
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), project);
var source = Path.Combine(root, "logic/pins.ve");
var original = Bom("# 原始中文注释\r\nHSECLK 8  # 实际晶振\r\nSYSCLK 200\r\nBUSCLK 100\r\n" +
    "PLLCLK1_PHASE 0 # 不由图形规划修改\r\n  GPIO4_4   PIN_21   # 已接线 LED\r\n\r\n");
await File.WriteAllBytesAsync(source, original);
var toolsetCatalog = new ToolsetCatalog(Path.GetFullPath(args[0]));
var service = new Ag32PinPlanningService(toolsetCatalog);
var checks = new List<string>();
void Check(bool value, string name)
{
    if (!value) { throw new InvalidOperationException(name); }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> action, string code, string name)
{
    var before = await File.ReadAllBytesAsync(source);
    try { await action(); throw new InvalidOperationException("Expected rejection: " + name); }
    catch (StudioXException ex) when (ex.Code == code)
    {
        var after = await File.ReadAllBytesAsync(source);
        Check(before.SequenceEqual(after), name + "; VE bytes unchanged");
    }
}
if (args.Length > 3 && args[3] == "--profiles-only")
{
    await RunProfilesAsync();
    await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { checks, count = checks.Count, success = true });
    Console.WriteLine($"Completed {checks.Count} profile checks; converter only, no license or hardware access.");
    return;
}
var snapshot = await service.ReadAsync(root);
Check(snapshot.CanEdit && snapshot.Pins.Length == 48 && snapshot.Pins.Count(pin => pin.CanAssign) == 34,
    "Actual converter returns exact LQFP48 editable pins");
Check(snapshot.Functions.Any(function => function.Name == "UART0_UARTTXD" && function.Direction == "OUTPUT" && function.SharedGpio == "GPIO7_6"),
    "Vendor function catalogue preserves peripheral direction and shared GPIO");
ConflictChecks.Run(snapshot, Check);
Check(snapshot.SourcePath == "logic/pins.ve" && snapshot.Clocks == new Ag32PinClockSettings(8, 200, 100),
    "Snapshot records project relative path and explicit clock values");
var applied = await service.ApplyAsync(root, snapshot.SourceSha256, [new("GPIO4_4", 2)], snapshot.Clocks);
var current = await File.ReadAllBytesAsync(source);
var text = Encoding.UTF8.GetString(current);
Check(current is [0xef, 0xbb, 0xbf, ..] && text.Contains("\r\n") && !text.Replace("\r\n", "").Contains('\n'),
    "UTF-8 BOM and CRLF preserved when changing PIN_21 to PIN_2");
Check(text.Contains("# 原始中文注释\r\n") && text.Contains("PLLCLK1_PHASE 0 # 不由图形规划修改\r\n") &&
    text.Contains("  GPIO4_4 PIN_2   # 已接线 LED\r\n"), "Original comments, unrelated settings and indentation preserved");
Check(applied.Snapshot.SourceSha256 == Convert.ToHexString(SHA256.HashData(current)), "Saved snapshot is bound to actual source bytes");
var vex = await File.ReadAllTextAsync(PathBoundary.Resolve(root, applied.VexPath));
var sdc = await File.ReadAllTextAsync(PathBoundary.Resolve(root, applied.SdcPath));
var header = await File.ReadAllTextAsync(Path.Combine(applied.ConstraintDirectory, "pins.hx"));
Check(vex.Contains("GPIO4_4") && vex.Contains("PIN_2"), "Actual vendor VEX output binds requested package pin");
Check(sdc.Contains("create_clock -name PIN_HSE -period 125 [get_ports PIN_HSE]") &&
    sdc.Contains("derive_pll_clocks -create_base_clocks") && header.Contains("BOARD_HSE_FREQUENCY 8000000"),
    "SDC is generated from actual 8 MHz converter header");
Check(!Directory.Exists(Path.Combine(root, ".build/ag32-mapping")) && !Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories).Any(),
    "Planning does not run Supra or produce downloadable firmware");
snapshot = applied.Snapshot;
Task<Ag32PinPlanResult> Apply(Ag32PinAssignment[] mappings, Ag32PinClockSettings? clocks = null)
    => service.ApplyAsync(root, snapshot.SourceSha256, mappings, clocks ?? snapshot.Clocks);
await Reject(async () => await Apply([new("GPIO4_4", 1)]), "AG32_PIN_PLAN_PIN", "Fixed package pin rejected");
await Reject(async () => await Apply([new("GPIO4_4", 49)]), "AG32_PIN_PLAN_PIN", "Out of package pin rejected");
await Reject(async () => await Apply([new("NOT_A_FUNCTION", 2)]), "AG32_PIN_PLAN_FUNCTION", "Unknown function rejected");
await Reject(async () => await Apply([new("GPIO4_4\nASSIGN attack", 2)]), "AG32_PIN_PLAN_FUNCTION", "VE injection rejected");
await Reject(async () => await Apply([new("GPIO4_4", 2, "INOUT;exec attack")]), "AG32_PIN_PLAN_FUNCTION", "Direction Tcl injection rejected");
await Reject(async () => await Apply([new("GPIO4_4", 2), new("GPIO4_5", 2)]), "AG32_PIN_PLAN_CONFLICT", "Shared physical pin rejected");
await Reject(async () => await Apply([new("GPIO4_4", 21), new("GPIO4_4", 2)]), "AG32_PIN_PLAN_CONFLICT", "Same GPIO on PIN_21 and PIN_2 rejected");
await Reject(async () => await service.ApplyAsync(root, snapshot, [new("GPIO4_4", 21), new("GPIO4_4", 2)], snapshot.Clocks),
    "AG32_PIN_PLAN_CONFLICT", "Snapshot bound apply rejects duplicate function before overwriting VE");
await Reject(async () => await Apply([new("GPIO7_6", 2), new("UART0_UARTTXD", 21)]), "AG32_PIN_PLAN_CONFLICT", "GPIO and peripheral sharing same resource rejected");
await Reject(async () => await Apply([new("UART0_UARTTXD", 2, "INPUT")]), "AG32_PIN_PLAN_FUNCTION", "Peripheral wrong direction rejected");
await Reject(async () => await Apply([new("GPIO4_4", 2)], new(-8, 200, 100)), "AG32_PIN_PLAN_CLOCK", "Negative external clock rejected");
await Reject(async () => await Apply([new("GPIO4_4", 2)], new(8, 1000, 100)), "AG32_PIN_PLAN_CLOCK", "System clock above verified device limit rejected");
await Reject(async () => await Apply([new("GPIO4_4", 2)], new(8, 200, 75)), "AG32_PIN_PLAN_CONVERTER", "Actual converter rejects incompatible BUSCLK");
await File.AppendAllTextAsync(source, "# 文本编辑器的修改\r\n");
await Reject(async () => await Apply([new("GPIO4_4", 21)]), "AG32_PIN_PLAN_STALE", "Stale graph cannot overwrite editor changes");
await File.WriteAllBytesAsync(source, Bom("# 用户自定义行\r\nCUSTOM_LOGIC signal\r\nGPIO4_4 PIN_2\r\n"));
var complex = await service.ReadAsync(root);
Check(!complex.CanEdit && complex.Diagnostics.Length != 0 && complex.Pins.Length == 48, "Unsupported custom VE stays visible and read only");
await Reject(async () => await service.ApplyAsync(root, complex.SourceSha256, [new("GPIO4_4", 21)], complex.Clocks),
    "AG32_PIN_PLAN_READONLY", "Unsupported configuration cannot be silently rewritten");
await File.WriteAllBytesAsync(source, Bom("GPIO4_4 PIN_2\r\n"));
snapshot = await service.ReadAsync(root);
var omitted = await service.ApplyAsync(root, snapshot.SourceSha256, [new("GPIO4_4", 21)], snapshot.Clocks);
Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(source)).Contains("HSECLK"), "Omitted clocks are not replaced by inferred defaults");
Check(Directory.GetDirectories(Path.Combine(root, ".build/ag32-pin-plan"), "plan-*").Length == 1,
    "Only latest successful constraint preview is retained");
await File.WriteAllBytesAsync(source, Bom("PLLPFD 8 ;exec attack\r\nGPIO4_4 PIN_2\r\n"));
var unsafeSetting = await service.ReadAsync(root);
Check(!unsafeSetting.CanEdit, "Unsafe VEX directive tail is read only");
await Reject(async () => await service.ApplyAsync(root, unsafeSetting.SourceSha256, [new("GPIO4_4", 21)], unsafeSetting.Clocks),
    "AG32_PIN_PLAN_READONLY", "Existing unsafe directive is not copied into new constraints");
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), project with { DeviceId = "STM32F407ZGT6", PinMapping = null });
await Reject(async () => await service.ReadAsync(root), "AG32_PIN_PLAN_DEVICE", "Non AGM project cannot use graphical mapping");
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), project);
await McpChecks.RunAsync(Directory.GetParent(Path.GetFullPath(args[0]))!.FullName, root, output, Check);
var textBuildRoot = Path.Combine(output, "text-clock-build");
Directory.CreateDirectory(Path.Combine(textBuildRoot, ".studiox"));
Directory.CreateDirectory(Path.Combine(textBuildRoot, "device"));
Directory.CreateDirectory(Path.Combine(textBuildRoot, "logic"));
await JsonStore.WriteAsync(Path.Combine(textBuildRoot, ".studiox/project.json"), project);
await JsonStore.WriteAsync(Path.Combine(textBuildRoot, "device/manifest.json"), pack);
var textBuildSource = Path.Combine(textBuildRoot, "logic/pins.ve");
var textBuilder = new Ag32PinMappingBuildService(toolsetCatalog, Path.Combine(output, "no-vendor-license"));
foreach (var (configuration, label) in new[]
{
    ("SYSCLK 1000\nBUSCLK 100\n", "Text VE build rejects overclocked SYSCLK"),
    ("SYSCLK,1000\n", "Comma separated VE cannot bypass clock limit"),
    ("SYSCLK 200\nBUSCLK 300\n", "Text VE build rejects overclocked BUSCLK"),
    ("SYSCLK NaN\n", "Text VE build rejects non finite clocks"),
    ("SYSCLK 200\nSYSCLK 160\n", "Text VE build rejects ambiguous repeated clock declarations")
})
{
    var sourceBytes = Bom(configuration + "GPIO4_4 PIN_2\n");
    await File.WriteAllBytesAsync(textBuildSource, sourceBytes);
    try
    {
        await textBuilder.BuildAsync(textBuildRoot);
        throw new InvalidOperationException("Expected text clock rejection.");
    }
    catch (StudioXException ex) when (ex.Code == "AG32_MAPPING_CLOCK")
    {
        var unchanged = await File.ReadAllBytesAsync(textBuildSource);
        Check(sourceBytes.SequenceEqual(unchanged) && !File.Exists(Path.Combine(textBuildRoot, ".build/ag32-mapping/pins.vx")),
            label + "; source unchanged, rejected before logic conversion");
    }
}
await File.WriteAllBytesAsync(textBuildSource, Bom("HSECLK 8\nSYSCLK 248\nBUSCLK 124\nPLLCLK1_PHASE 0 # 原有参数保留\nGPIO4_4 PIN_2\n"));
try
{
    await textBuilder.BuildAsync(textBuildRoot);
    throw new InvalidOperationException("Expected missing private license after valid converter.");
}
catch (StudioXException ex) when (ex.Code == "AG32_MAPPING_LICENSE")
{
    Check(File.ReadAllText(Path.Combine(textBuildRoot, ".build/ag32-mapping/pins.hx")).Contains("BOARD_PLL_FREQUENCY 248000000", StringComparison.Ordinal) &&
        !File.Exists(Path.Combine(textBuildRoot, ".build/ag32-mapping/pins.bin")),
        "Text VE boundary 248 MHz and preserved advanced parameter pass real converter without running Supra");
}
await RunProfilesAsync();
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { checks, count = checks.Count, success = true });
Console.WriteLine($"Completed {checks.Count} checks; converter only, no license or hardware access.");

static byte[] Bom(string value) => [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(value)];

async Task RunProfilesAsync()
{
    foreach (var candidateDevice in pack.Devices.Where(Ag32DeviceCatalog.Matches))
    {
        var profile = Ag32DeviceCatalog.Require(candidateDevice.Id);
        var candidateProject = project with
        {
            DeviceId = candidateDevice.Id, ToolsetId = candidateDevice.ToolsetId, ToolsetVersion = candidateDevice.ToolsetVersion,
            CompilerId = candidateDevice.CompilerId, PinMapping = new(profile.TargetDevice)
        };
        await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), candidateProject);
        await File.WriteAllTextAsync(source, "# 官方器件包逐型号转换\nGPIO4_4 PIN_2\n");
        var candidateSnapshot = await service.ReadAsync(root);
        Check(candidateSnapshot.TargetDevice == profile.TargetDevice && candidateSnapshot.Pins.Length == profile.PinCount && candidateSnapshot.CanEdit,
            $"{profile.DeviceId} obtains its exact {profile.TargetDevice} package and vendor pin list");
        var candidatePlan = await service.ApplyAsync(root, candidateSnapshot.SourceSha256, candidateSnapshot.Assignments, candidateSnapshot.Clocks);
        Check(File.ReadAllText(PathBoundary.Resolve(root, candidatePlan.VexPath)).Contains("PIN_2", StringComparison.Ordinal) &&
            File.Exists(PathBoundary.Resolve(root, candidatePlan.SdcPath)), $"{profile.DeviceId} actual converter produces VEX and SDC");
    }
}
