using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Length is < 3 or > 4) { throw new ArgumentException("Usage: <toolset root> <new validation output directory> <AG32 mcupack> [private license directory]"); }
var toolsets = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var archive = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) { throw new IOException("Validation output must be new."); }
Directory.CreateDirectory(output);
var root = Path.Combine(output, "中文工程 with spaces");
// 使用真实格式 1 器件包生成夹具，确保容量、厂商和工具锁定也接受与产品相同的身份检查。
var pack = await new PackRepository(Path.Combine(output, "repository")).ImportAsync(archive);
var project = await new ProjectService().CreateAsync(pack, "AG32VF303CCT6", "minimal", "mapping_test", root);
var source = Path.Combine(root, "logic/pins.ve");
var catalog = new ToolsetCatalog(toolsets);
var licenseSource = new Ag32PinMappingBuildService(catalog, args.Length > 3 ? args[3] : null);
var service = new Ag32PinMappingBuildService(catalog, Path.Combine(licenseSource.LicenseDirectory, "validation", Guid.NewGuid().ToString("N")));
await service.ImportLicenseAsync(Path.Combine(licenseSource.LicenseDirectory, "license.txt"));
var checks = new List<string>();
void Check(bool value, string name) { if (!value) { throw new InvalidOperationException(name); } checks.Add(name); Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string code, string name)
{
    try { await action(); throw new InvalidOperationException("Expected rejection: " + name); }
    catch (StudioXException ex) when (ex.Code == code) { Check(true, name); }
}
Check(service.LicenseConfigured, "Private vendor license configured; credentials never printed");
await File.WriteAllTextAsync(source, "# no active mapping\n");
await Reject(async () => await service.BuildAsync(root), "AG32_MAPPING_EMPTY", "Empty VE does not download defaults");
await File.WriteAllTextAsync(source, "GPIO4_4 PIN_1\n");
await Reject(async () => await service.BuildAsync(root), "AG32_MAPPING_PIN", "Fixed supply pin rejected");
await File.WriteAllTextAsync(source, "GPIO4_4 PIN_49\n");
await Reject(async () => await service.BuildAsync(root), "AG32_MAPPING_PIN", "Wrong package pin rejected");
await File.WriteAllTextAsync(source, "SYSCLK 200\nBUSCLK 100\nHSECLK 8\nGPIO4_4 PIN_21\n");
var first = await service.BuildAsync(root);
Check(first.Success && first.ExitCode == 0, "Real VE converter and Supra compile PIN_21 without Quartus");
var firstImage = await service.ValidateBuiltAsync(root);
var actualIoPath = Path.Combine(root, ".build/ag32-mapping/logic_db/io.asf");
var actualRoutedPath = Path.Combine(root, ".build/ag32-mapping/pins_routed.v");
Check((await File.ReadAllTextAsync(actualIoPath)).Contains("set_location_assignment -to GPIO4_4 PIN_21"), "Supra final IO placement binds GPIO4_4 to PIN_21");
Check((await File.ReadAllTextAsync(actualRoutedPath)).Contains(".padio(GPIO4_4)"), "Routed netlist connects actual IO buffer to GPIO4_4");
foreach (var (path, name) in new[] { (actualIoPath, "pin21-io.asf"), (actualRoutedPath, "pin21-routed.v"), (first.LogPath, "pin21-build.log") })
{
    File.Copy(path, Path.Combine(output, name));
}
Check(firstImage.ByteCount is > 0 and <= 102400 && firstImage.FlashAddress == 0x80027000, "Image within reserved logic region");
Check((await service.InspectAsync(root)).ReceiptCurrent, "Successful receipt available");
var timestamp = File.GetLastWriteTimeUtc(source);
await File.WriteAllTextAsync(source, "SYSCLK 200\nBUSCLK 100\nHSECLK 8\nGPIO4_4 PIN_2\n");
File.SetLastWriteTimeUtc(source, timestamp);
await Reject(async () => await service.ValidateBuiltAsync(root), "AG32_MAPPING_STALE", "VE content hash rejects old BIN despite restored timestamp");
var second = await service.BuildAsync(root);
var secondImage = await service.RequireImageAsync(root);
Check(second.Success && firstImage.Sha256 != secondImage.Sha256, "PIN_21 to PIN_2 changes actual generated bitstream");
Check((await File.ReadAllTextAsync(actualIoPath)).Contains("set_location_assignment -to GPIO4_4 PIN_2"), "Supra final IO placement binds GPIO4_4 to PIN_2");
Check(!second.Log.Contains("is not assigned, placed at pin", StringComparison.Ordinal), "Compiler has no unconstrained auto-placed IO");
foreach (var (path, name) in new[] { (actualIoPath, "pin2-io.asf"), (actualRoutedPath, "pin2-routed.v"), (second.LogPath, "pin2-build.log") })
{
    File.Copy(path, Path.Combine(output, name));
}
Check((await File.ReadAllTextAsync(Path.Combine(root, ".build/ag32-mapping/pins.vex"))).Contains("PIN_2"), "Generated VEX contains chosen package mapping");
var originalBin = await File.ReadAllBytesAsync(secondImage.Path);
await File.WriteAllBytesAsync(secondImage.Path, [0x00]);
await Reject(async () => await service.ValidateBuiltAsync(root), "AG32_MAPPING_IMAGE", "Tampered bitstream rejected");
await File.WriteAllBytesAsync(secondImage.Path, originalBin);
Check((await service.ValidateBuiltAsync(root)).Sha256 == secondImage.Sha256, "Matching bitstream restores receipt");
var originalIo = await File.ReadAllTextAsync(actualIoPath);
await File.WriteAllTextAsync(actualIoPath, originalIo.Replace("GPIO4_4 PIN_2", "GPIO4_4 PIN_37", StringComparison.Ordinal));
await Reject(async () => await service.ValidateBuiltAsync(root), "AG32_MAPPING_ROUTING", "Unconstrained or mismatched actual placement evidence rejected");
await File.WriteAllTextAsync(actualIoPath, originalIo);
var receiptPath = Path.Combine(root, ".build/ag32-mapping/studiox-mapping-receipt.json");
var receiptText = await File.ReadAllTextAsync(receiptPath);
await File.WriteAllTextAsync(receiptPath, receiptText.Replace("\"formatVersion\": 2", "\"formatVersion\": 1", StringComparison.Ordinal));
await Reject(async () => await service.ValidateBuiltAsync(root), "AG32_MAPPING_STALE", "Old receipt without physical routing evidence rejected");
await File.WriteAllTextAsync(receiptPath, receiptText);
var actualSdcPath = Path.Combine(root, ".build/ag32-mapping/studiox-clocks.sdc");
var sdcText = await File.ReadAllTextAsync(actualSdcPath);
Check(sdcText.Contains("create_clock -name PIN_HSE -period 125 "), "HSE 8 MHz receives accurate 125 ns input timing");
await File.WriteAllTextAsync(actualSdcPath, sdcText.Replace("PIN_HSE -period 125", "PIN_HSE -period 50", StringComparison.Ordinal));
await Reject(async () => await service.ValidateBuiltAsync(root), "AG32_MAPPING_CLOCK", "Incorrect 20 MHz input timing rejected");
await File.WriteAllTextAsync(actualSdcPath, sdcText);
var routedText = await File.ReadAllTextAsync(actualRoutedPath);
var changedRouted = System.Text.RegularExpressions.Regex.Replace(routedText, @"(defparam pll_inst\.CLKIN_HIGH\s*=\s*)8'b[01]+", "${1}8'b00000001");
Check(changedRouted != routedText, "Divider tamper fixture changes actual numeric bitfield");
await File.WriteAllTextAsync(actualRoutedPath, changedRouted);
await Reject(async () => await service.ValidateBuiltAsync(root), "AG32_MAPPING_CLOCK", "Changed final PLL divider bitfield rejected");
await File.WriteAllTextAsync(actualRoutedPath, routedText);
await File.WriteAllTextAsync(source, "SYSCLK 160\nBUSCLK 80\nHSECLK 8\nGPIO4_4 PIN_2\n");
var clock160 = await service.BuildAsync(root);
Check(clock160.Success && clock160.Log.Contains("50 项数字位参数"), "160/80 MHz design preserves all 50 final PLL divider fields");
Check((await File.ReadAllTextAsync(Path.Combine(root, ".build/ag32-mapping/pins.hx"))).Contains("BOARD_PLL_FREQUENCY 160000000"), "Generated clock target follows explicit VE frequency");
foreach (var (path, name) in new[] { (actualRoutedPath, "clock160-routed.v"), (actualSdcPath, "clock160.sdc"),
    (Path.Combine(root, ".build/ag32-mapping/pins.hx"), "clock160.hx"), (clock160.LogPath, "clock160-build.log") })
{
    File.Copy(path, Path.Combine(output, name));
}
var noLicense = new Ag32PinMappingBuildService(catalog, Path.Combine(Path.GetTempPath(), "StudioX-mapping-validation-no-license-" + Guid.NewGuid().ToString("N")));
await Reject(async () => await noLicense.BuildAsync(root), "AG32_MAPPING_LICENSE", "Missing license reports cause and invalidates old receipt");
Check(!File.Exists(secondImage.Path), "Failed mapping build removes obsolete BIN");
await Reject(async () => await service.ValidateBuiltAsync(root), "AG32_MAPPING_BUILD_REQUIRED", "Failure cannot fall back to an older receipt");
await File.WriteAllTextAsync(source, "GPIO99_0 PIN_2\n");
var failed = await service.BuildAsync(root);
Check(!failed.Success && failed.Log.Contains("Error", StringComparison.OrdinalIgnoreCase), "Raw vendor invalid-function diagnosis retained");
await File.WriteAllTextAsync(source, "GPIO4_4 PIN_2\n", new System.Text.UTF8Encoding(true));
Check((await service.BuildAsync(root)).Success, "UTF-8 BOM accepted without modifying source");
Check((await File.ReadAllBytesAsync(source)).AsSpan(0, 3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Editor source retains BOM");
using (var cancel = new CancellationTokenSource())
{
    try
    {
        await service.BuildAsync(root, new ImmediateProgress(message => { if (message.Contains("独立映射镜像", StringComparison.Ordinal)) { cancel.Cancel(); } }), cancel.Token);
        throw new InvalidOperationException("Expected cancellation");
    }
    catch (OperationCanceledException) { Check(true, "Cancellation before Supra invalidates receipt and removes private staging"); }
}
Check(!File.Exists(secondImage.Path), "Cancelled build leaves no downloadable image");
Check(!Directory.Exists(Path.Combine(service.LicenseDirectory, "runs")) || !Directory.EnumerateFileSystemEntries(Path.Combine(service.LicenseDirectory, "runs")).Any(), "Private license staging cleaned after success, failure and cancellation");
Check((await service.BuildAsync(root)).Success, "Fresh build succeeds after cancellation");
await File.WriteAllTextAsync(source, "LED_OUT PIN_2:OUTPUT\n");
Check(!(await service.BuildAsync(root)).Success, "Custom top-level signal cannot silently become an empty mapping");
await File.WriteAllTextAsync(source, "GPIO4_4 BAD_PIN\nGPIO3_3 PIN_21\n");
Check(!(await service.BuildAsync(root)).Success, "Invalid destination cannot be ignored alongside another valid mapping");
await File.WriteAllTextAsync(source, "ASSIGN A B\nGPIO4_4 PIN_2\n");
await Reject(async () => await service.BuildAsync(root), "AG32_MAPPING_CUSTOM_LOGIC", "Custom ASSIGN logic rejected in basic mapping");
await File.WriteAllTextAsync(source, "GPIO4_4 PIN_2\n");
await Reject(async () => await service.BuildAsync(root, new ImmediateProgress(message =>
{
    if (message.Contains("独立映射镜像", StringComparison.Ordinal)) { File.WriteAllText(source, "GPIO4_4 PIN_21\n"); }
})), "AG32_MAPPING_CHANGED", "VE edited during compilation cannot receive a valid receipt");
Check(!File.Exists(secondImage.Path), "Changed-during-build image is removed");
await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), project with { Logic = new("AGRV2KL48", "logic/user_logic.v", "logic/pins.ve") });
await Reject(async () => await service.BuildAsync(root), "AG32_MAPPING_CUSTOM_LOGIC", "Basic backend never replaces custom Verilog");
Check(!File.Exists(secondImage.Path), "Custom logic rejection invalidates basic image");
await JsonStore.WriteAsync(Path.Combine(output, "results.json"), new { hardwareConnected = false, realVendorCompile = true, checks = checks.ToArray(), firstImage.Sha256, pin2Sha256 = secondImage.Sha256 });
File.Delete(Path.Combine(service.LicenseDirectory, "license.txt"));
if (Directory.Exists(Path.Combine(service.LicenseDirectory, "runs"))) { Directory.Delete(Path.Combine(service.LicenseDirectory, "runs")); }
Directory.Delete(service.LicenseDirectory);
Console.WriteLine($"All {checks.Count} checks passed.");

sealed class ImmediateProgress(Action<string> action) : IProgress<string>
{
    public void Report(string value) => action(value);
}
