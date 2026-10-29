using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Arduino;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Engine.Arduino;
using StudioX.Foundation;

var reuseData = args.Length == 6 && args[5] == "--reuse-data";
if (reuseData)
{
    args = args[..5];
}
if (args is not [var evidence, var runtime, var data, var avrPack, var espPacks]) { throw new ArgumentException("<new-evidence> <runtime> <user-data> <AVR-pack> <ESP-pack-directory> [--reuse-data]"); }
evidence = Path.GetFullPath(evidence);
runtime = Path.GetFullPath(runtime);
data = Path.GetFullPath(data);
if (Directory.Exists(evidence) || !reuseData && Directory.Exists(data)) { throw new ArgumentException("Use new validation directories."); }
Directory.CreateDirectory(evidence);
var checks = new List<string>();
void Check(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
    checks.Add(message);
    Console.WriteLine("PASS " + message);
}
await using var services = new WorkbenchService(runtime, data);
var avr = await services.Packs.ImportAsync(avrPack);
var arduinoDevice = avr.Manifest.Devices.Single();
Check(ArduinoTemplateCatalog.Templates(arduinoDevice).Single().DisplayName == "Arduino", "ATmega328P has Arduino in the common template catalog");
var packs = new Dictionary<string, StudioX.Packages.InstalledPack>();
foreach (var name in new[] { "esp32-wroom-32", "esp32c3", "esp32c5", "esp32c6", "esp32s3", "esp32p4" })
{
    packs[name] = await services.Packs.ImportAsync(Path.Combine(espPacks, "espressif." + name + "-0.1.1.mcupack"));
}
foreach (var entry in packs)
{
    var device = entry.Value.Manifest.Devices.Single();
    var choices = ArduinoTemplateCatalog.Templates(device);
    Check(choices.Any(ArduinoTemplateCatalog.IsArduino) == (entry.Key != "esp32p4"), device.Id + " offers only adapted framework targets");
    Check(device.Templates.All(item => choices.Contains(item)), device.Id + " preserves native SDK templates");
}
var unrelated = packs["esp32c3"].Manifest.Devices.Single() with { Id = "STM32F407ZGT6", Architecture = "arm", Espressif = null };
Check(!ArduinoTemplateCatalog.Templates(unrelated).Any(ArduinoTemplateCatalog.IsArduino), "unadapted chips retain their native templates without a fake Arduino choice");
var projects = new Dictionary<string, string>();
foreach (var board in ArduinoBoard.Supported)
{
    var pack = board.PlatformId == "avr" ? avr : packs.Values.Single(item => item.Manifest.Devices.Single().Id == board.DeviceId);
    var name = "Template_" + board.Id.Replace('-', '_');
    var root = Path.Combine(evidence, "中文 parent with spaces", name);
    var created = await services.Arduino.CreateFromPackAsync(pack, board.DeviceId, board.Id, name, root);
    Check(created.Kind == ProjectKind.Arduino && created.DeviceId == board.DeviceId && created.PackContentHash == pack.ContentHash &&
        created.ToolsetId == board.Platform.ToolsetId && created.Espressif is null, board.Id + " locks selected device and Arduino toolchain without an IDF project");
    Check(Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, "CMakeLists.txt")), board.Id + " uses native sketch and initializes bundled Git");
    var health = await services.ProjectHealth.InspectAsync(root);
    Check(health.CanBuild && health.Checks.Any(item => item.Code == "HEALTH_PROJECT_SCOPE" && item.Detail == ProjectCapabilities.For(created).Scope), board.Id + " preflight uses common support scope");
    var build = await services.Builds.BuildAsync(root);
    await File.WriteAllTextAsync(Path.Combine(evidence, board.Id + ".log"), build.Log);
    Check(build.Success, board.Id + " real Arduino compilation under Unicode and spaces");
    Check(!Directory.Exists(Path.Combine(root, "build")), board.Id + " keeps core export side effects outside sketch source");
    var preview = await services.Arduino.PreviewUploadAsync(root);
    Check(preview.Bytes > 0 && preview.HexPath.EndsWith(board.FirmwareSuffix, StringComparison.Ordinal), board.Id + " preflight uses the platform firmware type");
    if (board.PlatformId == "esp32")
    {
        var receipt = await JsonStore.ReadAsync<ArduinoBuildReceipt>(Path.Combine(root, ArduinoBuildReceipt.RelativePath));
        Check(receipt.UploadFiles is { Count: >= 3 }, board.Id + " protects ESP32 multi-image output");
        var partitions = Path.Combine(Path.GetDirectoryName(preview.HexPath)!, name + ".ino.partitions.bin");
        var original = await File.ReadAllBytesAsync(partitions);
        await File.AppendAllTextAsync(partitions, "changed");
        try
        {
            await services.Arduino.PreviewUploadAsync(root);
            throw new InvalidOperationException("Changed partitions accepted");
        }
        catch (StudioXException error) when (error.Code == "ARDUINO_BUILD_STALE") { Check(true, board.Id + " rejects changed partitions before upload"); }
        await File.WriteAllBytesAsync(partitions, original);
    }
    projects[board.Id] = root;
}
try
{
    await services.Arduino.CreateFromPackAsync(packs["esp32c3"], "ESP32-C3", "uno", "WrongBoard", Path.Combine(evidence, "WrongBoard"));
    throw new InvalidOperationException("Wrong board accepted");
}
catch (StudioXException error) when (error.Code == "ARDUINO_DEVICE") { Check(!Directory.Exists(Path.Combine(evidence, "WrongBoard")), "cross-chip board selection rejected without creating project"); }
var tools = await services.Toolsets.ResolveAsync(ArduinoPlatform.Esp32.ToolsetId, ArduinoPlatform.Esp32.CoreVersion, ArduinoPlatform.Esp32.CompilerId);
var cachedBoards = Path.Combine(data, "arduino", "r", tools.Fingerprint[..20], "data", "packages", "esp32", "hardware", "esp32", "3.3.8", "boards.txt");
var cachedBytes = await File.ReadAllBytesAsync(cachedBoards);
await File.AppendAllTextAsync(cachedBoards, "\nchanged");
try
{
    await services.Arduino.PreviewUploadAsync(projects["esp32c3"]);
    throw new InvalidOperationException("Changed cached core accepted");
}
catch (StudioXException error) when (error.Code == "ARDUINO_CACHE_CHANGED") { Check(true, "private core metadata changes revoke memoized verification and trigger content validation"); }
finally { await File.WriteAllBytesAsync(cachedBoards, cachedBytes); }
await using (var language = new CodeIntelligenceService(runtime, data))
{
    await language.StartAsync(projects["esp32c3"]);
    var source = "#include <Arduino.h>\nvoid setup() { pinM";
    var completion = await language.CompleteAsync("Template_esp32c3.ino", source, source.Length);
    Check(completion.Any(item => item.Label.Contains("pinMode", StringComparison.Ordinal)), "ESP32-C3 real clangd offers Arduino core API completion");
    var valid = "#include <Arduino.h>\nvoid setup() { Serial.begin(115200); }\nvoid loop() { delay(1); }\n";
    await language.SynchronizeDiagnosticsAsync("Template_esp32c3.ino", valid, []);
    for (var attempt = 0; attempt < 40 && !language.GetDiagnostics().Any(batch => batch.Text == valid && batch.IsComplete); attempt++)
    {
        await Task.Delay(100);
    }
    await File.WriteAllTextAsync(Path.Combine(evidence, "diagnostics.json"), JsonSerializer.Serialize(language.GetDiagnostics(), new JsonSerializerOptions { WriteIndented = true }));
    Check(language.GetDiagnostics().Any(batch => batch.Text == valid && batch.IsComplete && batch.Items.All(item => item.Severity != 1)), "valid ESP32 Arduino sketch has no false core/header errors");
    await File.WriteAllLinesAsync(Path.Combine(evidence, "language.log"), language.DrainLog());
}
await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { passed = true, realCompilation = "passed", packEvidence = packs.Values.Append(avr).Select(pack => new { pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash }), hardware = false, checks, projects }, new JsonSerializerOptions { WriteIndented = true }));
